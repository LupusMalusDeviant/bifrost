using Xunit;

// Der Probelauf startet einen echten Gateway-Host und echte Upstream-Prozesse — parallele
// Testklassen wuerden sich dabei ueber Ports und Prozess-Baselines in die Quere kommen.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
