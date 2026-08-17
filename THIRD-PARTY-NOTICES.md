# Third-party notices

Vyral's authored source and client libraries are Apache-2.0. Dependencies remain
under their own terms and are resolved by the package ecosystem used by each
consumer. Every release includes a CycloneDX SBOM so exact dependency versions
can be reviewed with the release artifact.

The release review currently calls out packages whose SBOM metadata does not
carry a machine-readable license expression even though the distributed package
does contain its license file:

| Package family | License evidence | License |
| --- | --- | --- |
| `Microsoft.Extensions.Logging.Abstractions` 2.2.0 | [Package-declared upstream `LICENSE`](https://raw.githubusercontent.com/aspnet/AspNetCore/2.0.0/LICENSE.txt) | Apache-2.0 |
| `Microsoft.ML.OnnxRuntime` and `Microsoft.ML.OnnxRuntime.Managed` | Distributed `LICENSE` / `LICENSE.txt` | MIT |
| `SQLite` | Distributed `LICENSE.txt` and [SQLite copyright notice](https://sqlite.org/copyright.html) | Public domain |
| `SourceGear.sqlite3` | Distributed `LICENSE.txt` and SQLite copyright notice | Public domain |
| `System.Reactive.Compatibility` | [Upstream version-tagged `LICENSE`](https://github.com/dotnet/reactive/blob/rxnet-v4.4.1/LICENSE) | MIT |

Apache-2.0, MIT, BSD-3-Clause, PostgreSQL, and public-domain dependencies are
compatible with this repository's Apache-2.0 distribution. `Unknown - See URL`
entries in an SBOM require release-owner review before publication; an SBOM is
an inventory, not a substitute for reviewing the applicable package terms.

This document is a release-review aid, not legal advice.
