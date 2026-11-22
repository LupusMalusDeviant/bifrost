using Xunit;

// Jeder Browserweg startet eine eigene Instanz mit eigenem Port und eigenen Upstream-Prozessen.
// Parallel gefahren wuerden sie sich Ports und Prozesszaehlungen streitig machen.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
