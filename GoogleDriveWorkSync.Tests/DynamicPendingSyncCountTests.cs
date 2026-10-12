using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services.Interfaces;
using GoogleDriveWorkSync.ViewModels;
using Moq;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

/// <summary>
/// Independent Cleanroom Black-Box test suite formulated strictly from the formal contract
/// specification in `docs/contracts/dynamic-pending-sync-count.contract.md`.
/// </summary>
public class DynamicPendingSyncCountTests
{
    #region Test Harness Helpers

    private static (WorkSyncViewModel ViewModel, Mock<IDriveSyncService> MockService) CreateViewModel(
        int initialPending = 0,
        int initialNew = 0,
        int initialModified = 0,
        List<OutOfSyncFile>? initialFiles = null)
    {
        var mockService = new Mock<IDriveSyncService>();
        mockService.Setup(s => s.IsConfigured).Returns(true);
        mockService.Setup(s => s.PreviewOutOfSyncAsync(It.IsAny<CancellationToken>(), It.IsAny<SyncSource?>()))
                   .Returns(new TaskCompletionSource<IReadOnlyList<OutOfSyncFile>>().Task);

        var vm = new WorkSyncViewModel(mockService.Object)
        {
            OutOfSyncCount = initialPending,
            NewFilesCount = initialNew,
            ModifiedFilesCount = initialModified,
            HasOutOfSyncFiles = initialPending > 0
        };

        if (initialFiles != null)
        {
            foreach (var file in initialFiles)
            {
                vm.OutOfSyncFilesList.Add(file);
            }
        }

        return (vm, mockService);
    }

    private sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static async Task RunWithProgressAsync(
        WorkSyncViewModel vm,
        Mock<IDriveSyncService> mockService,
        Func<IProgress<SyncProgressReport>, Task> reportAction)
    {
        var progressReporter = new DirectProgress<SyncProgressReport>(report =>
        {
            mockService.Raise(s => s.SyncProgressChanged += null, mockService.Object, report);
        });

        await reportAction(progressReporter);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Assertion condition was not met within the expected time limit.");
            }
            await Task.Delay(20);
        }
    }

    #endregion

    #region Acceptance Criterion 1: Regressive Pending Count Decrement & SyncActionTitle

    [Fact]
    public async Task ProgressReport_WhenPendingFilesRemain_DecrementsOutOfSyncCountAndSetsSyncingTitle()
    {
        // Given: Sync session starts with 10 pending files
        var (vm, mockService) = CreateViewModel(initialPending: 10);

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: 3 files are reported uploaded (RemainingPendingFiles = 7)
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 10,
                UploadedCount = 3,
                ErrorCount = 0
            });

            await WaitForConditionAsync(() => vm.OutOfSyncCount == 7);

            // Then: OutOfSyncCount updates to remaining files, HasOutOfSyncFiles is true, and title matches contract
            Assert.Equal(7, vm.OutOfSyncCount);
            Assert.True(vm.HasOutOfSyncFiles);
            Assert.Equal("Sincronizando (7 pendientes)...", vm.SyncActionTitle);
        });
    }

    [Fact]
    public async Task ProgressReport_WhenAllFilesUploaded_DecrementsToZeroAndSetsCompletingTitle()
    {
        // Given: Sync session starts with 5 pending files
        var (vm, mockService) = CreateViewModel(initialPending: 5);

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: All 5 files are reported uploaded (RemainingPendingFiles = 0)
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 5,
                UploadedCount = 5,
                ErrorCount = 0
            });

            await WaitForConditionAsync(() => vm.OutOfSyncCount == 0);

            // Then: OutOfSyncCount is 0, HasOutOfSyncFiles is false, and title is "Completando sincronización..."
            Assert.Equal(0, vm.OutOfSyncCount);
            Assert.False(vm.HasOutOfSyncFiles);
            Assert.Equal("Completando sincronización...", vm.SyncActionTitle);
        });
    }

    [Fact]
    public async Task ProgressReport_MultipleSequentialBatches_ProgressivelyDecrementsPendingCountAndUpdatesTitle()
    {
        // Given: Sync session starts with 12 pending files
        var (vm, mockService) = CreateViewModel(initialPending: 12);

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // Batch 1: 4 uploaded -> 8 remaining
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 12,
                UploadedCount = 4,
                ErrorCount = 0
            });
            await WaitForConditionAsync(() => vm.OutOfSyncCount == 8);
            Assert.Equal(8, vm.OutOfSyncCount);
            Assert.Equal("Sincronizando (8 pendientes)...", vm.SyncActionTitle);

            // Batch 2: 8 uploaded -> 4 remaining
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 12,
                UploadedCount = 8,
                ErrorCount = 0
            });
            await WaitForConditionAsync(() => vm.OutOfSyncCount == 4);
            Assert.Equal(4, vm.OutOfSyncCount);
            Assert.Equal("Sincronizando (4 pendientes)...", vm.SyncActionTitle);

            // Batch 3: 12 uploaded -> 0 remaining
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 12,
                UploadedCount = 12,
                ErrorCount = 0
            });
            await WaitForConditionAsync(() => vm.OutOfSyncCount == 0);
            Assert.Equal(0, vm.OutOfSyncCount);
            Assert.False(vm.HasOutOfSyncFiles);
            Assert.Equal("Completando sincronización...", vm.SyncActionTitle);
        });
    }

    #endregion

    #region Acceptance Criterion 2: Granular Decrement of New vs Modified Files

    [Fact]
    public async Task ProgressReport_WhenCompletedItemIsNewAndSuccessful_DecrementsNewFilesCountOnly()
    {
        // Given: NewFilesCount = 5, ModifiedFilesCount = 3
        var (vm, mockService) = CreateViewModel(initialPending: 8, initialNew: 5, initialModified: 3);

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: CompletedItems has a successfully uploaded new file
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 8,
                UploadedCount = 1,
                CompletedItems = new List<CompletedSyncItem>
                {
                    new() { FilePath = @"C:\work\new_doc.txt", FileName = "new_doc.txt", IsNew = true, Success = true }
                }
            });

            await WaitForConditionAsync(() => vm.NewFilesCount == 4);

            // Then: NewFilesCount is decremented by 1, ModifiedFilesCount remains unchanged
            Assert.Equal(4, vm.NewFilesCount);
            Assert.Equal(3, vm.ModifiedFilesCount);
        });
    }

    [Fact]
    public async Task ProgressReport_WhenCompletedItemIsModifiedAndSuccessful_DecrementsModifiedFilesCountOnly()
    {
        // Given: NewFilesCount = 5, ModifiedFilesCount = 3
        var (vm, mockService) = CreateViewModel(initialPending: 8, initialNew: 5, initialModified: 3);

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: CompletedItems has a successfully uploaded modified file
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 8,
                UploadedCount = 1,
                CompletedItems = new List<CompletedSyncItem>
                {
                    new() { FilePath = @"C:\work\edited.docx", FileName = "edited.docx", IsNew = false, Success = true }
                }
            });

            await WaitForConditionAsync(() => vm.ModifiedFilesCount == 2);

            // Then: ModifiedFilesCount is decremented by 1, NewFilesCount remains unchanged
            Assert.Equal(5, vm.NewFilesCount);
            Assert.Equal(2, vm.ModifiedFilesCount);
        });
    }

    [Fact]
    public async Task ProgressReport_WhenCompletedItemFailed_DoesNotDecrementNewOrModifiedCount()
    {
        // Given: NewFilesCount = 4, ModifiedFilesCount = 4
        var (vm, mockService) = CreateViewModel(initialPending: 8, initialNew: 4, initialModified: 4);

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: CompletedItems contains failed uploads for both new and modified files
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 8,
                UploadedCount = 0,
                ErrorCount = 2,
                CompletedItems = new List<CompletedSyncItem>
                {
                    new() { FilePath = @"C:\work\fail_new.txt", FileName = "fail_new.txt", IsNew = true, Success = false },
                    new() { FilePath = @"C:\work\fail_mod.txt", FileName = "fail_mod.txt", IsNew = false, Success = false }
                }
            });

            // Allow event pipeline to settle
            await Task.Delay(100);

            // Then: Neither counter is decremented for failed items
            Assert.Equal(4, vm.NewFilesCount);
            Assert.Equal(4, vm.ModifiedFilesCount);
        });
    }

    [Fact]
    public async Task ProgressReport_WhenDecrementsExceedCurrentCount_ClampsToZeroAndNeverBecomesNegative()
    {
        // Given: NewFilesCount = 1, ModifiedFilesCount = 0
        var (vm, mockService) = CreateViewModel(initialPending: 1, initialNew: 1, initialModified: 0);

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: Batch reports 3 successful new files and 1 successful modified file
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 1,
                UploadedCount = 4,
                CompletedItems = new List<CompletedSyncItem>
                {
                    new() { FilePath = @"C:\work\1.txt", FileName = "1.txt", IsNew = true, Success = true },
                    new() { FilePath = @"C:\work\2.txt", FileName = "2.txt", IsNew = true, Success = true },
                    new() { FilePath = @"C:\work\3.txt", FileName = "3.txt", IsNew = true, Success = true },
                    new() { FilePath = @"C:\work\mod.txt", FileName = "mod.txt", IsNew = false, Success = true }
                }
            });

            await WaitForConditionAsync(() => vm.NewFilesCount == 0);

            // Then: Counters clamp strictly at zero (non-negative invariant)
            Assert.Equal(0, vm.NewFilesCount);
            Assert.Equal(0, vm.ModifiedFilesCount);
        });
    }

    [Fact]
    public async Task ProgressReport_MixedBatchWithSuccessAndFailure_DecrementsOnlySuccessfulItems()
    {
        // Given: NewFilesCount = 6, ModifiedFilesCount = 6
        var (vm, mockService) = CreateViewModel(initialPending: 12, initialNew: 6, initialModified: 6);

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: Mixed batch containing 2 successful new, 1 failed new, 1 successful mod, 1 failed mod
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 12,
                UploadedCount = 3,
                ErrorCount = 2,
                CompletedItems = new List<CompletedSyncItem>
                {
                    new() { FilePath = @"C:\work\new_ok1.txt", FileName = "new_ok1.txt", IsNew = true, Success = true },
                    new() { FilePath = @"C:\work\new_ok2.txt", FileName = "new_ok2.txt", IsNew = true, Success = true },
                    new() { FilePath = @"C:\work\new_err.txt", FileName = "new_err.txt", IsNew = true, Success = false },
                    new() { FilePath = @"C:\work\mod_ok1.txt", FileName = "mod_ok1.txt", IsNew = false, Success = true },
                    new() { FilePath = @"C:\work\mod_err.txt", FileName = "mod_err.txt", IsNew = false, Success = false }
                }
            });

            await WaitForConditionAsync(() => vm.NewFilesCount == 4 && vm.ModifiedFilesCount == 5);

            // Then: New decrements by 2 (6 -> 4), Modified decrements by 1 (6 -> 5)
            Assert.Equal(4, vm.NewFilesCount);
            Assert.Equal(5, vm.ModifiedFilesCount);
        });
    }

    #endregion

    #region Acceptance Criterion 3: Real-Time In-Memory List Pruning from OutOfSyncFilesList

    [Fact]
    public async Task ProgressReport_WhenItemSucceeds_RemovesItemFromOutOfSyncFilesList()
    {
        // Given: OutOfSyncFilesList contains F1, F2, F3
        var file1 = new OutOfSyncFile { FilePath = @"C:\work\f1.txt", FileName = "f1.txt", Reason = "Nuevo" };
        var file2 = new OutOfSyncFile { FilePath = @"C:\work\f2.txt", FileName = "f2.txt", Reason = "Modificado" };
        var file3 = new OutOfSyncFile { FilePath = @"C:\work\f3.txt", FileName = "f3.txt", Reason = "Nuevo" };

        var (vm, mockService) = CreateViewModel(initialPending: 3, initialFiles: new List<OutOfSyncFile> { file1, file2, file3 });

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: F2 is completed with Success = true
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 3,
                UploadedCount = 1,
                CompletedItems = new List<CompletedSyncItem>
                {
                    new() { FilePath = @"C:\work\f2.txt", FileName = "f2.txt", IsNew = false, Success = true }
                }
            });

            await WaitForConditionAsync(() => vm.OutOfSyncFilesList.Count == 2);

            // Then: F2 is pruned; F1 and F3 remain in the list
            Assert.Equal(2, vm.OutOfSyncFilesList.Count);
            Assert.DoesNotContain(vm.OutOfSyncFilesList, f => f.FilePath == @"C:\work\f2.txt");
            Assert.Contains(vm.OutOfSyncFilesList, f => f.FilePath == @"C:\work\f1.txt");
            Assert.Contains(vm.OutOfSyncFilesList, f => f.FilePath == @"C:\work\f3.txt");
        });
    }

    [Fact]
    public async Task ProgressReport_WhenItemFails_KeepsItemInOutOfSyncFilesList()
    {
        // Given: OutOfSyncFilesList contains F1, F2
        var file1 = new OutOfSyncFile { FilePath = @"C:\work\f1.txt", FileName = "f1.txt", Reason = "Nuevo" };
        var file2 = new OutOfSyncFile { FilePath = @"C:\work\f2.txt", FileName = "f2.txt", Reason = "Modificado" };

        var (vm, mockService) = CreateViewModel(initialPending: 2, initialFiles: new List<OutOfSyncFile> { file1, file2 });

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: F1 completes with Success = false
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 2,
                UploadedCount = 0,
                ErrorCount = 1,
                CompletedItems = new List<CompletedSyncItem>
                {
                    new() { FilePath = @"C:\work\f1.txt", FileName = "f1.txt", IsNew = true, Success = false }
                }
            });

            await Task.Delay(100);

            // Then: Items with Success == false are NOT removed
            Assert.Equal(2, vm.OutOfSyncFilesList.Count);
            Assert.Contains(vm.OutOfSyncFilesList, f => f.FilePath == @"C:\work\f1.txt");
            Assert.Contains(vm.OutOfSyncFilesList, f => f.FilePath == @"C:\work\f2.txt");
        });
    }

    [Fact]
    public async Task ProgressReport_WhenCompletedItemNotInList_DoesNotThrowAndHandlesSafely()
    {
        // Given: OutOfSyncFilesList has F1 (e.g., beyond preview limit of 200 items)
        var file1 = new OutOfSyncFile { FilePath = @"C:\work\f1.txt", FileName = "f1.txt", Reason = "Nuevo" };

        var (vm, mockService) = CreateViewModel(initialPending: 500, initialFiles: new List<OutOfSyncFile> { file1 });

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: Progress report reports a file not present in the preview list
            var exception = await Record.ExceptionAsync(async () =>
            {
                progress.Report(new SyncProgressReport
                {
                    TotalPendingFiles = 500,
                    UploadedCount = 1,
                    CompletedItems = new List<CompletedSyncItem>
                    {
                        new() { FilePath = @"C:\work\ghost_file.txt", FileName = "ghost_file.txt", IsNew = true, Success = true }
                    }
                });
                await Task.Delay(100);
            });

            // Then: No exception occurs, and existing list elements remain intact
            Assert.Null(exception);
            Assert.Single(vm.OutOfSyncFilesList);
            Assert.Equal(@"C:\work\f1.txt", vm.OutOfSyncFilesList[0].FilePath);
        });
    }

    [Fact]
    public async Task ProgressReport_BatchWithMultipleSuccessfulAndFailedItems_PrunesOnlySuccessfulItems()
    {
        // Given: OutOfSyncFilesList has F1, F2, F3, F4
        var f1 = new OutOfSyncFile { FilePath = @"C:\work\f1.txt", FileName = "f1.txt", Reason = "Nuevo" };
        var f2 = new OutOfSyncFile { FilePath = @"C:\work\f2.txt", FileName = "f2.txt", Reason = "Modificado" };
        var f3 = new OutOfSyncFile { FilePath = @"C:\work\f3.txt", FileName = "f3.txt", Reason = "Nuevo" };
        var f4 = new OutOfSyncFile { FilePath = @"C:\work\f4.txt", FileName = "f4.txt", Reason = "Modificado" };

        var (vm, mockService) = CreateViewModel(initialPending: 4, initialFiles: new List<OutOfSyncFile> { f1, f2, f3, f4 });

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: Batch reports F1 (Success), F2 (Failed), F3 (Success)
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 4,
                UploadedCount = 2,
                ErrorCount = 1,
                CompletedItems = new List<CompletedSyncItem>
                {
                    new() { FilePath = @"C:\work\f1.txt", FileName = "f1.txt", IsNew = true, Success = true },
                    new() { FilePath = @"C:\work\f2.txt", FileName = "f2.txt", IsNew = false, Success = false },
                    new() { FilePath = @"C:\work\f3.txt", FileName = "f3.txt", IsNew = true, Success = true }
                }
            });

            await WaitForConditionAsync(() => vm.OutOfSyncFilesList.Count == 2);

            // Then: F1 and F3 are pruned; F2 (failed) and F4 (not yet processed) remain
            Assert.Equal(2, vm.OutOfSyncFilesList.Count);
            Assert.Contains(vm.OutOfSyncFilesList, f => f.FilePath == @"C:\work\f2.txt");
            Assert.Contains(vm.OutOfSyncFilesList, f => f.FilePath == @"C:\work\f4.txt");
            Assert.DoesNotContain(vm.OutOfSyncFilesList, f => f.FilePath == @"C:\work\f1.txt");
            Assert.DoesNotContain(vm.OutOfSyncFilesList, f => f.FilePath == @"C:\work\f3.txt");
        });
    }

    #endregion

    #region Acceptance Criterion 4: Edge Cases & Contract Invariants

    [Fact]
    public async Task ProgressReport_WhenTotalPendingFilesIsZero_OutOfSyncCountRemainsZeroAndNoRemovalsOccur()
    {
        // Given: Sync runs with 0 pending files
        var (vm, mockService) = CreateViewModel(initialPending: 0);

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: Progress report published with TotalPendingFiles = 0
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 0,
                UploadedCount = 0,
                ErrorCount = 0,
                CompletedItems = Array.Empty<CompletedSyncItem>()
            });

            await Task.Delay(100);

            // Then: OutOfSyncCount remains 0, HasOutOfSyncFiles is false
            Assert.Equal(0, vm.OutOfSyncCount);
            Assert.False(vm.HasOutOfSyncFiles);
            Assert.Empty(vm.OutOfSyncFilesList);
        });
    }

    [Fact]
    public async Task ProgressReport_WhenCompletedItemsIsEmpty_DoesNotThrowAndDoesNotAlterList()
    {
        // Given: OutOfSyncFilesList has items
        var f1 = new OutOfSyncFile { FilePath = @"C:\work\f1.txt", FileName = "f1.txt", Reason = "Nuevo" };
        var (vm, mockService) = CreateViewModel(initialPending: 5, initialFiles: new List<OutOfSyncFile> { f1 });

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: Batch report arrives with empty CompletedItems
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 5,
                UploadedCount = 1,
                CompletedItems = Array.Empty<CompletedSyncItem>()
            });

            await WaitForConditionAsync(() => vm.OutOfSyncCount == 4);

            // Then: Pending count updates properly, but list remains unaltered
            Assert.Equal(4, vm.OutOfSyncCount);
            Assert.Single(vm.OutOfSyncFilesList);
            Assert.Equal(@"C:\work\f1.txt", vm.OutOfSyncFilesList[0].FilePath);
        });
    }

    [Fact]
    public async Task ProgressReport_NegativeCountProtection_OutOfSyncCountNeverNegative()
    {
        // Given: Pending files initial state
        var (vm, mockService) = CreateViewModel(initialPending: 3);

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            // When: Report where UploadedCount + ErrorCount > TotalPendingFiles
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 3,
                UploadedCount = 4,
                ErrorCount = 2
            });

            await WaitForConditionAsync(() => vm.OutOfSyncCount == 0);

            // Then: Clamped to 0 (non-negative bound Math.Max(0, ...))
            Assert.Equal(0, vm.OutOfSyncCount);
            Assert.False(vm.HasOutOfSyncFiles);
        });
    }

    [Fact]
    public async Task SyncCompleted_ExecutesPreviewOutOfSyncAsSourceOfTruthReconciliation()
    {
        // Given: Initial sync configuration
        var (vm, mockService) = CreateViewModel(initialPending: 2);

        await RunWithProgressAsync(vm, mockService, async progress =>
        {
            progress.Report(new SyncProgressReport
            {
                TotalPendingFiles = 2,
                UploadedCount = 2
            });
            await WaitForConditionAsync(() => vm.OutOfSyncCount == 0);
        });

        // Then: When sync completion fires, PreviewOutOfSyncAsync is invoked as reconciliation against physical disk
        mockService.Verify(s => s.PreviewOutOfSyncAsync(It.IsAny<CancellationToken>(), It.IsAny<SyncSource?>()), Times.AtLeastOnce);
    }

    #endregion

    #region Model Specification Tests: CompletedSyncItem & SyncProgressReport

    [Fact]
    public void CompletedSyncItem_DefaultProperties_InitializedProperly()
    {
        var item = new CompletedSyncItem();

        Assert.Equal(string.Empty, item.FilePath);
        Assert.Equal(string.Empty, item.FileName);
        Assert.False(item.IsNew);
        Assert.False(item.Success);
    }

    [Fact]
    public void SyncProgressReport_RemainingPendingFiles_CalculatesDifferenceCorrectly()
    {
        var report = new SyncProgressReport
        {
            TotalPendingFiles = 25,
            UploadedCount = 10,
            ErrorCount = 5
        };

        // 25 - 10 - 5 = 10
        Assert.Equal(10, report.RemainingPendingFiles);
    }

    [Fact]
    public void SyncProgressReport_RemainingPendingFiles_ClampsAtZeroWhenSumExceedsTotal()
    {
        var report = new SyncProgressReport
        {
            TotalPendingFiles = 5,
            UploadedCount = 6,
            ErrorCount = 2
        };

        // Math.Max(0, 5 - 6 - 2) = 0
        Assert.Equal(0, report.RemainingPendingFiles);
    }

    [Fact]
    public void SyncProgressReport_RemainingPendingFiles_WhenZeroTotal_ReturnsZero()
    {
        var report = new SyncProgressReport
        {
            TotalPendingFiles = 0,
            UploadedCount = 0,
            ErrorCount = 0
        };

        Assert.Equal(0, report.RemainingPendingFiles);
    }

    [Fact]
    public void SyncProgressReport_CompletedItems_DefaultsToEmptyListNotNull()
    {
        var report = new SyncProgressReport();

        Assert.NotNull(report.CompletedItems);
        Assert.Empty(report.CompletedItems);
    }

    [Fact]
    public void SyncProgressReport_Percentage_WhenTotalFilesIsZero_Returns100()
    {
        var report = new SyncProgressReport
        {
            TotalFiles = 0,
            ProcessedFiles = 0
        };

        Assert.Equal(100, report.Percentage);
    }

    [Theory]
    [InlineData(10, 5, 50)]
    [InlineData(3, 1, 33)]
    [InlineData(3, 2, 67)]
    [InlineData(10, 10, 100)]
    public void SyncProgressReport_Percentage_CalculatesRoundedProgress(int total, int processed, int expected)
    {
        var report = new SyncProgressReport
        {
            TotalFiles = total,
            ProcessedFiles = processed
        };

        Assert.Equal(expected, report.Percentage);
    }

    #endregion
}
