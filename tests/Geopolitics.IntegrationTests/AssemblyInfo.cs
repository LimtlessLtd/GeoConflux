// Each test here boots a full application host from the same content root. Hosts started in
// parallel contend over that shared state — most visibly the default relative SQLite path used
// while the entry point is being resolved — which makes results depend on scheduling rather than on
// the behaviour under test. Running the assembly serially keeps these tests deterministic; the
// suite takes a few seconds, so there is nothing to gain from overlapping them.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
