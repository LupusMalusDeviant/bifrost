using Xunit;

// Soak-Laeufe starten echte Prozesse, zaehlen Kindprozesse und toeten welche. Parallel dazu einen
// zweiten Lauf zu fahren hiesse, die Aufraeumprobe des einen mit den Prozessen des anderen zu
// beantworten.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
