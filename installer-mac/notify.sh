# GUI feedback from a postinstall script, which normally can't show any: installer scripts run
# as root with no window-server session, so a plain `osascript -e ...` fails silently.
# `launchctl asuser` re-targets the call at the logged-in console user's session, the standard
# workaround for this. Sourced by both postinstall scripts so a fix here fixes both.

# Runs a command in the console user's session; empty/root console user (headless, login
# screen) means there's no session to target, so callers just skip the notification.
_run_as_console_user() {
  local console_user console_uid
  console_user="$(stat -f%Su /dev/console 2>/dev/null || true)"
  [ -z "$console_user" ] || [ "$console_user" = "root" ] && return 1
  console_uid="$(id -u "$console_user" 2>/dev/null || true)"
  [ -z "$console_uid" ] && return 1
  launchctl asuser "$console_uid" sudo -u "$console_user" "$@"
}

# Blocking alert with an OK button — for things the user actually needs to read and
# acknowledge, like a failure. Confirmed working: this is what showed the "SnapZap.app was not
# found in /Applications" dialog during testing.
notify_user() {
  local title="$1" message="$2"
  _run_as_console_user osascript -e "display alert \"$title\" message \"$message\"" >/dev/null 2>&1 || true
}

# A passing Notification Center banner — no click required, doesn't block the installer wizard.
# For "this is normal, just slow" reassurance, where forcing a click would be one more thing in
# the way for no reason.
notify_banner() {
  local title="$1" message="$2"
  _run_as_console_user osascript -e "display notification \"$message\" with title \"$title\"" >/dev/null 2>&1 || true
}
