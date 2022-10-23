﻿using BlazorDiffusion.ServiceModel;
using BlazorDiffusion.UI;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ServiceStack.Blazor;

namespace BlazorDiffusion.Shared;

public partial class ArtifactMenu : AppAuthComponentBase
{
    [Inject] public NavigationManager NavigationManager { get; set; } = default!;
    [Inject] public UserState UserState { get; set; }

    const int OffsetY = 60;
    const int OffsetX = 60;

    enum ArtifactView
    {
        Report,
        NewAlbum,
    }

    ArtifactView? artifactView;

    ApiResult<ArtifactReport> apiReport = new();
    string[] ReportVisibleFields => new[] {
        nameof(ArtifactReport.Type),
        nameof(ArtifactReport.Description),
    };
    CreateArtifactReport requestReport = new();

    ApiResult<Album> apiNewAlbum = new();
    string[] NewAlbumVisibleFields => new[] {
        nameof(CreateAlbum.Name),
    };
    CreateAlbum newAlbumRequest = new();


    async Task<bool> assertAuth()
    {
        if (!IsAuthenticated)
        {
            await OnDone();
            NavigationManager.NavigateTo(NavigationManager.GetLoginUrl(), true);
            return false;
        }
        return true;
    }

    async Task openReport()
    {
        if (!await assertAuth())
            return;

        requestReport = new();
        artifactView = ArtifactView.Report;
    }

    async Task submitReport()
    {
        requestReport.ArtifactId = Artifact.Id;
        apiReport = await ApiAsync(requestReport);
        if (apiReport.Succeeded)
        {
            await OnDone();
        }
    }

    async Task openNewAlbum()
    {
        if (!await assertAuth())
            return;

        newAlbumRequest = new();
        artifactView = ArtifactView.NewAlbum;
    }

    async Task submitNewAlbum()
    {
        newAlbumRequest.ArtifactIds = new() { Artifact.Id };
        newAlbumRequest.PrimaryArtifactId = Artifact.Id;
        apiNewAlbum = await ApiAsync(newAlbumRequest);
        if (apiNewAlbum.Succeeded)
        {
            await OnDone();
        }
    }



    [Parameter, EditorRequired] public Artifact Artifact { get; set; } = default!;
    [Parameter, EditorRequired] public MouseEventArgs Position { get; set; } = default!;

    [Parameter] public bool Show { get; set; }

    [Parameter] public EventCallback Done { get; set; }

    async Task addToAlbum()
    {
        var request = new UpdateAlbum
        {
            AddArtifactIds = new() {  Artifact.Id },
        };
        var api = await ApiAsync(request); 
        if (api.Succeeded)
        {
            await OnDone();
        }
    }

    async Task OnDone()
    {
        artifactView = null;
        await Done.InvokeAsync();
    }

}
