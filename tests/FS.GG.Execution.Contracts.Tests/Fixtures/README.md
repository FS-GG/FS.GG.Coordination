# Disposable contract fixtures

These separately identified .2-C fixtures reuse finite ordinary-v1 byte and lifetime
meanings. They are inputs to the test-only caller in
`tests/engineering/process-contract-conformance`, not xUnit test discovery or a
production process backend. Existing 69 deterministic controls stay unchanged.

Every fixture waits at most one second for the literal stdin byte `G`, then reads
that single byte. This lets the retained caller observe the original Linux birth before
releasing target output. Invalid or missing release exits 64 without target output.
Normal literal arguments and environment are checked by the child before it emits
its marker and all 256 binary byte values on both channels. The dual and overflow
fixtures emit 256 or 32768 bytes per stream. Deadline/cancel sleepers and inherited
writers self-expire after 1.5 seconds. The forked writer stays in the inherited group;
no fixture sends signals, changes sessions or starts an unbounded child.

The inherited-writer case requires a separately qualified disposable owner/profile
before launch. Finite self-expiry is fixture behavior, not custody, containment or
reaping proof. The current proposal leaves this prerequisite unresolved and grants
no execution. Historical evaluator and failed CliWrap acquisitions remain unchanged.
