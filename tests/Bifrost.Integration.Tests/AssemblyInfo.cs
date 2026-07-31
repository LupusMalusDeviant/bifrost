using Xunit;

// Integrationstests starten und zählen echte Prozesse — Parallelausführung würde
// Prozess-Baselines und Ports verfälschen. Sequenziell ist hier Pflicht.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
