# Orchestration runtime comparison fixture

Run the pinned, self-checking laboratory from the repository root:

```bash
cd tests/Orchestration.RuntimeComparison
TIMEFORMAT='wall_seconds=%3R'; time dotnet run --project Orchestration.RuntimeComparison.csproj --configuration Release
```

The first Temporal run downloads its official local dev-server binary to the user cache. The
fixture has no provider credentials or mutation adapter. Akka's in-memory journal and Temporal's test
server are local laboratories; neither is PostgreSQL or production evidence. A nonzero exit reports that
one candidate could not execute, and the output must not be promoted to server-backed comparison evidence.
