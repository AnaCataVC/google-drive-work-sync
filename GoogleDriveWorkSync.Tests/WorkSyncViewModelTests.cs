using System;
using System.IO;
using System.Threading.Tasks;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services.Interfaces;
using GoogleDriveWorkSync.ViewModels;
using Moq;
using Xunit;

namespace GoogleDriveWorkSync.Tests;

public class WorkSyncViewModelTests
{
    [Fact]
    public void GetDisplayErrorMessage_WhenExceptionIsNull_ShouldReturnFallback()
    {
        var result = WorkSyncViewModel.GetDisplayErrorMessage(null!);
        Assert.Equal("Error inesperado durante la operación.", result);
    }

    [Fact]
    public void GetDisplayErrorMessage_WhenMessageIsEmpty_ShouldFallbackToTypeName()
    {
        var ex = new InvalidOperationException("   ");
        var result = WorkSyncViewModel.GetDisplayErrorMessage(ex);
        Assert.Equal("InvalidOperationException", result);
    }

    [Fact]
    public void GetDisplayErrorMessage_WhenMultiline_ShouldSanitizeToSingleLine()
    {
        var ex = new Exception("Error al conectar:\r\nDetalle en linea 2\nDetalle en linea 3");
        var result = WorkSyncViewModel.GetDisplayErrorMessage(ex);
        Assert.Equal("Error al conectar: Detalle en linea 2 Detalle en linea 3", result);
    }

    [Fact]
    public void GetDisplayErrorMessage_WhenAggregateException_ShouldExtractInnerMessage()
    {
        var inner = new HttpRequestException("Servidor de Apps Script no responde (503).");
        var aggEx = new AggregateException("One or more errors occurred.", inner);

        var result = WorkSyncViewModel.GetDisplayErrorMessage(aggEx);
        Assert.Equal("Servidor de Apps Script no responde (503).", result);
    }

    [Fact]
    public async Task SyncDriveNow_WhenServiceThrows_ShouldDisplayFormattedErrorMessage()
    {
        var mockService = new Mock<IDriveSyncService>();
        mockService.Setup(s => s.IsConfigured).Returns(true);
        mockService.Setup(s => s.PreviewOutOfSyncAsync(It.IsAny<System.Threading.CancellationToken>(), It.IsAny<SyncSource?>()))
                   .ReturnsAsync(new System.Collections.Generic.List<OutOfSyncFile>());
        mockService.Setup(s => s.RunSyncAsync(It.IsAny<IProgress<SyncProgressReport>?>(), It.IsAny<System.Threading.CancellationToken>(), It.IsAny<bool>(), It.IsAny<SyncSource?>()))
                   .ThrowsAsync(new IOException("El archivo está siendo utilizado por otro proceso."));

        var vm = new WorkSyncViewModel(mockService.Object);

        await vm.SyncDriveNow();

        Assert.Contains("Error al sincronizar:", vm.DriveSyncDetailText);
        Assert.Contains("El archivo está siendo utilizado por otro proceso.", vm.DriveSyncDetailText);
        Assert.False(string.IsNullOrWhiteSpace(vm.DriveSyncDetailText.Replace("Error al sincronizar:", "").Trim()));
    }

    [Fact]
    public async Task SyncDriveNow_WhenServiceThrowsWithEmptyMessage_ShouldDisplayTypeName()
    {
        var mockService = new Mock<IDriveSyncService>();
        mockService.Setup(s => s.IsConfigured).Returns(true);
        mockService.Setup(s => s.PreviewOutOfSyncAsync(It.IsAny<System.Threading.CancellationToken>(), It.IsAny<SyncSource?>()))
                   .ReturnsAsync(new System.Collections.Generic.List<OutOfSyncFile>());
        mockService.Setup(s => s.RunSyncAsync(It.IsAny<IProgress<SyncProgressReport>?>(), It.IsAny<System.Threading.CancellationToken>(), It.IsAny<bool>(), It.IsAny<SyncSource?>()))
                   .ThrowsAsync(new System.Runtime.InteropServices.COMException(string.Empty, unchecked((int)0x8001010E)));

        var vm = new WorkSyncViewModel(mockService.Object);

        await vm.SyncDriveNow();

        Assert.Contains("Error al sincronizar:", vm.DriveSyncDetailText);
        Assert.Contains("COMException", vm.DriveSyncDetailText);
    }
}
