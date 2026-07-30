// Several tests drive Config through the QUILL_CONFIG environment variable,
// which is process-global state. xunit already runs tests within a class
// sequentially, but it parallelizes across classes — so parallelization is off
// suite-wide rather than relying on every future test class to stay clear of it.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
