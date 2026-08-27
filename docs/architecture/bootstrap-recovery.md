# Bootstrap recovery

GS2-01.8 is the repository's clean-machine recovery proof. The closed Q7 command
`dotnet fsi eng/bootstrap-recovery.fsx -- .` refuses a dirty caller, resolves the
exact committed revision, and makes a non-local detached clone in private scratch
storage. Build products, developer caches, and uncommitted files therefore cannot
enter the proof.

The runner creates isolated .NET and NuGet homes and an explicit NuGet
configuration whose only dependency source is `https://api.nuget.org/v3/index.json`.
It restores the committed lock graph, builds once with warnings as errors, runs
both test suites without restore or rebuild, packs the Protocol project at the
inert bootstrap version, and installs it into the clean consumer through a local
candidate feed plus NuGet.org. The package is never published.

On success it writes canonical compact
`fsgg.coordination.bootstrap-recovery/1` evidence beneath ignored `artifacts/`.
That receipt binds the exact candidate, package SHA-256, published source, and the
ordered clone-through-execute stages. The read-only hosted recovery job uploads
the receipt, and the existing bootstrap evidence manifest binds its exact bytes.
The roadmap catalog and unit index independently pin the one admitted command;
caller overrides, feed substitution, GitHub writes, settings, deployments, and
GS2-01.9 behavior remain outside the permission ceiling.
