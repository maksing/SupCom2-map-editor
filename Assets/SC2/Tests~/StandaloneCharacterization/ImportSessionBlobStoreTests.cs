using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>
/// SC2MapImportPanel used to construct its own
/// private SC2DirectoryBlobStore (a separate root from SC2MapSession.
/// BlobStore) and import into that instead. Since import hands the result
/// to the SAME session via SetDocument, any blob written during import was
/// silently unreachable afterward through session.BlobStore.OpenRead() —
/// these tests exercise the fixed wiring (one shared store, constructed the
/// same way SC2MapImportPanel's constructor now does) end-to-end, not just
/// SC2MapImportService in isolation (MapImportServiceTests.cs already
/// covers that the service's OWN store receives the bytes; this file
/// covers that the SESSION's store — the one the exporter and Save/Load
/// actually read from — can resolve them too).
/// </summary>
public class ImportSessionBlobStoreTests
{
    [Fact]
    public async Task ImportIntoSharedSessionStore_EveryArchiveEntryBlobResolvesViaSessionBlobStore()
    {
        var blobRoot = Directory.CreateTempSubdirectory("sc2sessionblobs_").FullName;
        var session = new SC2MapSession(new SC2MapDocument())
        {
            BlobStore = new SC2DirectoryBlobStore(blobRoot),
        };
        // Mirrors SC2MapImportPanel's constructor: the import service must
        // be built against session.BlobStore, not a private store of its own.
        var service = new SC2MapImportService(new SC2SourceContainerReaderFactory(), session.BlobStore);

        var zipPath = ImportServiceTestFixtures.WriteZipFixture(ImportServiceTestFixtures.BuildFixtureEntries());
        try
        {
            var probe = await service.ProbeScdAsync(zipPath, null, CancellationToken.None);
            var valid = probe.ScenarioCandidates.Single(c => c.BdfStem == ImportServiceTestFixtures.ValidStem);
            var result = await service.ImportSelectedScenarioAsync(probe, valid.ScenarioEntryPath, null, null, CancellationToken.None);

            // Same pattern SC2MapImportPanel.ImportSelected() uses to hand
            // the freshly imported document back to the live session.
            session.SetDocument(result.Document);

            var entriesWithBlobs = session.Document.Archive.Entries
                .Where(e => !e.OriginalContent.IsEmpty)
                .ToList();
            Assert.NotEmpty(entriesWithBlobs);

            foreach (var entry in entriesWithBlobs)
            {
                Assert.True(session.BlobStore.Contains(entry.OriginalContent.ContentHash),
                    $"Blob for '{entry.NormalizedPath}' (hash {entry.OriginalContent.ContentHash}) " +
                    "is not reachable via session.BlobStore — it was written to a different store than " +
                    "the one the live session (and therefore export/save) actually reads from.");

                using var stream = session.BlobStore.OpenRead(entry.OriginalContent.ContentHash);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                Assert.Equal(entry.OriginalContent.Length, ms.Length);
            }
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            Directory.Delete(blobRoot, true);
        }
    }
}
