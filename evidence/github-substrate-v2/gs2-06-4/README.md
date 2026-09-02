# GS2-06.4 immutable execution pins

`corpus.json` seals the complete two-workflow Coordination corpus at
`e25727a89ad0101188da74414669a556059d251e`, the accepted GS2-06.3 receipt,
the accepted roadmap at `.github@7ab43852609563265291eec2b4010a829582d447`,
and the exact organization Renovate policy bytes at that same accepted host revision.

Every distinct external Action reference in the corpus uses a full 40-hex commit.
Every literal `uses:` token is classified; repository-local `./...` execution
references are rejected because GitHub cannot bind that spelling to a full commit.
The corpus contains no reusable-workflow caller or `workflow_call` publication yet;
the contract therefore retains exact zero counts while defining the immutable
repository/path/commit/content tuple required as soon as either appears. Renovate is
the only automated updater authority and may propose `github-actions` changes only
through pull requests. This evidence grants no workflow publication or GitHub write.
