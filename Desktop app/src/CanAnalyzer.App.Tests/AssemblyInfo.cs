using Xunit;

// WPF resource packages are shared process-wide. Independent STA dispatchers
// must not load the same XAML resources concurrently across test classes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
