using System.Runtime.CompilerServices;

// The test project validates internal helpers (e.g. the defensive Czkawka JSON parser)
// without widening the public API.
[assembly: InternalsVisibleTo("SnapZap.Tests")]
