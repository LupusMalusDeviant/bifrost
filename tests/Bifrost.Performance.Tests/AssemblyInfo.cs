using Xunit;

// Lastmessungen teilen sich die Maschine mit nichts: Parallel laufende Testklassen wuerden
// gegeneinander messen, und das Ergebnis waere die Auslastung des Rechners statt des Gateways.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
