#!/usr/bin/env bash
# Builds the SnapZap macOS installer end to end: publish, assemble the .app bundle, package as a
# .pkg with an optional explicit-content-model component. The macOS analogue of
# scripts/build-installer.bat + installer/SnapZap.iss.
#
#   scripts/build-installer-mac.sh              publish and build the installer
#   scripts/build-installer-mac.sh --no-build   use the existing artifacts/mac as-is
#
# Output: artifacts/installer-mac/SnapZap-<version>.pkg
#
# Two things are structurally different from the Windows installer, not just missing:
#
# 1. This can never ship self-contained. The apphost binary `dotnet publish` produces is
#    ad-hoc signed and unnotarized, and gets SIGKILLed by endpoint security the instant it
#    launches — verified for both self-contained and framework-dependent publishes, see
#    CLAUDE.md's macOS build section. So SnapZap.app's executable is installer-mac/launcher.sh,
#    a shell script that hands off to the machine's own signed, notarized `dotnet` instead of
#    ours. That means the installed app depends on the .NET 10 runtime already being on the
#    machine — postinstall-app.sh only warns when it's missing, it cannot silently install it
#    the way the Windows installer bootstraps WebView2.
#
# 2. A .pkg has no built-in uninstaller. Removing SnapZap is dragging /Applications/SnapZap.app
#    to the Trash, same as any other unsigned Mac app. The catalogue and thumbnails in
#    ~/Library/Application Support/SnapZap are left behind either way, exactly as documented for
#    the app itself.
#
# Also unlike Windows: without an Apple Developer ID to codesign and notarize this package,
# Gatekeeper will flag it as being from an unidentified developer on any Mac other than the one
# that built it. Fine for this machine or handing to a few people; not a public download.
set -euo pipefail

# Otherwise `cp` leaves an AppleDouble ._* sidecar beside every script it copies, and pkgbuild
# bundles those into the package's Scripts directory right along with the real files.
export COPYFILE_DISABLE=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/.." && pwd)"
INSTALLER_SRC="$REPO/installer-mac"
STAGING="$REPO/artifacts/mac-pkg-staging"
OUT_DIR="$REPO/artifacts/installer-mac"
BUNDLE_ID="dev.moonwolf.snapzap"

SKIP_BUILD=0
[ "${1:-}" = "--no-build" ] && SKIP_BUILD=1

VERSION="$(grep -m1 '<Version>' "$REPO/src/SnapZap.App/SnapZap.App.csproj" \
  | sed -E 's/.*<Version>(.*)<\/Version>.*/\1/')"
echo "SnapZap version $VERSION"

if ! rm -rf "$STAGING" "$OUT_DIR" 2>/dev/null; then
  echo "ERROR: couldn't clear a previous build's leftovers under $STAGING or $OUT_DIR." >&2
  echo "  Something in there is owned by another user (root, most likely). Remove it yourself:" >&2
  echo "    sudo rm -rf \"$STAGING\" \"$OUT_DIR\"" >&2
  exit 1
fi
mkdir -p "$OUT_DIR"

APP_ROOT="$STAGING/app-root"
APP_BUNDLE="$APP_ROOT/SnapZap.app"
CONTENTS="$APP_BUNDLE/Contents"
MACOS="$CONTENTS/MacOS"
RESOURCES="$CONTENTS/Resources"
PAYLOAD="$RESOURCES/app"
mkdir -p "$MACOS" "$RESOURCES" "$PAYLOAD"

# --- publish ----------------------------------------------------------------------------
if [ "$SKIP_BUILD" -eq 0 ]; then
  echo "Publishing osx-arm64 (framework-dependent)..."
  dotnet publish "$REPO/src/SnapZap.App" -c Release -r osx-arm64 --self-contained false \
    -o "$PAYLOAD"
else
  if [ ! -f "$REPO/artifacts/mac/SnapZap.App.dll" ]; then
    echo "ERROR: --no-build needs an existing artifacts/mac publish (run without --no-build first)." >&2
    exit 1
  fi
  echo "Reusing existing artifacts/mac publish..."
  cp -R "$REPO/artifacts/mac/." "$PAYLOAD/"
fi

# --- .app bundle --------------------------------------------------------------------------
echo "Assembling SnapZap.app..."
cp "$INSTALLER_SRC/launcher.sh" "$MACOS/SnapZap"
chmod +x "$MACOS/SnapZap"
cp "$INSTALLER_SRC/find-dotnet.sh" "$RESOURCES/find-dotnet.sh"

if [ ! -f "$REPO/assets/icon/snapzap.icns" ]; then
  echo "ERROR: assets/icon/snapzap.icns is missing — run scripts/make-icons.py on macOS first." >&2
  exit 1
fi
cp "$REPO/assets/icon/snapzap.icns" "$RESOURCES/SnapZap.icns"

sed "s/@VERSION@/$VERSION/g" "$INSTALLER_SRC/Info.plist.template" > "$CONTENTS/Info.plist"
xattr -cr "$APP_ROOT"

# --- component: app -----------------------------------------------------------------------
echo "Building component packages..."
COMPONENTS="$STAGING/components"
mkdir -p "$COMPONENTS"

APP_SCRIPTS="$STAGING/scripts-app"
mkdir -p "$APP_SCRIPTS"
cp "$INSTALLER_SRC/postinstall-app.sh" "$APP_SCRIPTS/postinstall"
cp "$INSTALLER_SRC/find-dotnet.sh" "$APP_SCRIPTS/find-dotnet.sh"
cp "$INSTALLER_SRC/notify.sh" "$APP_SCRIPTS/notify.sh"
chmod +x "$APP_SCRIPTS/postinstall"
# Not COPYFILE_DISABLE's job: pkgbuild adds its own ._* AppleDouble sidecars into the package
# when a source file carries extended attributes (com.apple.provenance, picked up from however
# these files were last written) — strip them so it has nothing to shadow.
xattr -cr "$APP_SCRIPTS"

pkgbuild \
  --root "$APP_ROOT" \
  --identifier "$BUNDLE_ID.app" \
  --version "$VERSION" \
  --install-location /Applications \
  --scripts "$APP_SCRIPTS" \
  "$COMPONENTS/SnapZap-app.pkg"

# --- component: model (no payload — postinstall downloads it, same pins as install-deps.sh) --
MODEL_SCRIPTS="$STAGING/scripts-model"
mkdir -p "$MODEL_SCRIPTS"
cp "$INSTALLER_SRC/postinstall-model.sh" "$MODEL_SCRIPTS/postinstall"
cp "$INSTALLER_SRC/notify.sh" "$MODEL_SCRIPTS/notify.sh"
cp "$SCRIPT_DIR/install-deps.sh" "$MODEL_SCRIPTS/install-deps.sh"
chmod +x "$MODEL_SCRIPTS/postinstall" "$MODEL_SCRIPTS/install-deps.sh"
xattr -cr "$MODEL_SCRIPTS"

pkgbuild \
  --nopayload \
  --identifier "$BUNDLE_ID.model" \
  --version "$VERSION" \
  --scripts "$MODEL_SCRIPTS" \
  "$COMPONENTS/SnapZap-model.pkg"

# --- distribution (adds the customize pane: app fixed+selected, model optional) -----------
RESOURCES_DIR="$STAGING/resources"
mkdir -p "$RESOURCES_DIR"
cp "$REPO/LICENSE" "$RESOURCES_DIR/LICENSE.txt"

cat > "$STAGING/distribution.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="1">
    <title>SnapZap</title>
    <license file="LICENSE.txt" mime-type="text/plain"/>
    <options customize="always" require-scripts="false" rootVolumeOnly="true"/>
    <!-- Forces a system-wide install at /Applications and removes the "Destination Select"
         page. Without this, productbuild also offers a per-user install, which silently
         relocates the /Applications install-location pkgbuild baked into SnapZap-app.pkg to
         somewhere under the user's home — and then postinstall-model.sh's hardcoded
         /Applications/SnapZap.app check correctly reports the app as missing, because with a
         per-user install it genuinely is. -->
    <domains enable_currentUserHome="false" enable_localSystem="true" enable_anywhere="false"/>
    <choices-outline>
        <line choice="app"/>
        <line choice="model"/>
    </choices-outline>
    <choice id="app" title="SnapZap" selected="true" enabled="false">
        <pkg-ref id="$BUNDLE_ID.app"/>
    </choice>
    <choice id="model" title="Explicit-content scoring model"
            description="Downloads 328 MB during install (shows no progress bar — the installer only shows &quot;Running package scripts...&quot; while it fetches). Listed size is 0 KB because nothing is bundled into the package itself; SnapZap works fully without it either way.">
        <pkg-ref id="$BUNDLE_ID.model"/>
    </choice>
    <pkg-ref id="$BUNDLE_ID.app" version="$VERSION" onConclusion="none">SnapZap-app.pkg</pkg-ref>
    <pkg-ref id="$BUNDLE_ID.model" version="$VERSION" onConclusion="none">SnapZap-model.pkg</pkg-ref>
</installer-gui-script>
XML

productbuild \
  --distribution "$STAGING/distribution.xml" \
  --package-path "$COMPONENTS" \
  --resources "$RESOURCES_DIR" \
  "$OUT_DIR/SnapZap-$VERSION.pkg"

echo
echo "Built $OUT_DIR/SnapZap-$VERSION.pkg"
