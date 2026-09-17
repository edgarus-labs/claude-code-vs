using System.Runtime.CompilerServices;

// Exposes internal types (e.g. DiffBuilder) to the portable net10.0 test host so they can be
// exercised directly; the WPF (net472) Core project only ever consumes the public surface.
[assembly: InternalsVisibleTo("ClaudeCode.Core.Tests")]
