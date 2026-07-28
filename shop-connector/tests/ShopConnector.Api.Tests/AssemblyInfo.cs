using Xunit;

// One host, one Sqlite file, one provider_status table. The kill-switch test
// pauses a provider for real, so classes running concurrently would see each
// other's state rather than their own.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
