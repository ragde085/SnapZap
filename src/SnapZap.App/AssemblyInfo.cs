using System.Runtime.CompilerServices;

// The App project holds real logic worth testing directly — session persistence, the folder
// tree rollup, dependency resolution — not just markup.
[assembly: InternalsVisibleTo("SnapZap.Tests")]
