using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Remote Images Controller.
/// </summary>
[Route("")]
public class RemoteImageController : BaseJellyfinApiController
{
    private readonly IProviderManager _providerManager;
    private readonly IServerApplicationPaths _applicationPaths;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteImageController"/> class.
    /// </summary>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="applicationPaths">Instance of the <see cref="IServerApplicationPaths"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    public RemoteImageController(
        IProviderManager providerManager,
        IServerApplicationPaths applicationPaths,
        ILibraryManager libraryManager)
    {
        _providerManager = providerManager;
        _applicationPaths = applicationPaths;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets available remote images for an item.
    /// </summary>
    /// <param name="itemId">Item Id.</param>
    /// <param name="type">The image type.</param>
    /// <param name="startIndex">Optional. The record index to start at. All items with a lower index will be dropped from the results.</param>
    /// <param name="limit">Optional. The maximum number of records to return.</param>
    /// <param name="providerName">Optional. The image provider to use.</param>
    /// <param name="includeAllLanguages">Optional. Include all languages.</param>
    /// <response code="200">Remote Images returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>Remote Image Result.</returns>
    [HttpGet("Items/{itemId}/RemoteImages")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RemoteImageResult>> GetRemoteImages(
        [FromRoute, Required] Guid itemId,
        [FromQuery] ImageType? type,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery] string? providerName,
        [FromQuery] bool includeAllLanguages = false)
    {
        var item = _libraryManager.GetItemById<BaseItem>(itemId, User.GetUserId());
        if (item is null)
        {
            return NotFound();
        }

        var images = await _providerManager.GetAvailableRemoteImages(
                item,
                new RemoteImageQuery(providerName ?? string.Empty)
                {
                    IncludeAllLanguages = includeAllLanguages,
                    IncludeDisabledProviders = true,
                    ImageType = type
                },
                CancellationToken.None)
            .ConfigureAwait(false);

        var imageArray = images.ToArray();
        var allProviders = _providerManager.GetRemoteImageProviderInfo(item);
        if (type.HasValue)
        {
            allProviders = allProviders.Where(o => o.SupportedImages.Contains(type.Value));
        }

        var result = new RemoteImageResult
        {
            TotalRecordCount = imageArray.Length,
            Providers = allProviders.Select(o => o.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

        if (startIndex.HasValue)
        {
            imageArray = imageArray.Skip(startIndex.Value).ToArray();
        }

        if (limit.HasValue)
        {
            imageArray = imageArray.Take(limit.Value).ToArray();
        }

        result.Images = imageArray;
        return result;
    }

    /// <summary>
    /// Gets available remote image providers for an item.
    /// </summary>
    /// <param name="itemId">Item Id.</param>
    /// <response code="200">Returned remote image providers.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>List of remote image providers.</returns>
    [HttpGet("Items/{itemId}/RemoteImages/Providers")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IEnumerable<ImageProviderInfo>> GetRemoteImageProviders([FromRoute, Required] Guid itemId)
    {
        var item = _libraryManager.GetItemById<BaseItem>(itemId, User.GetUserId());
        if (item is null)
        {
            return NotFound();
        }

        return Ok(_providerManager.GetRemoteImageProviderInfo(item));
    }

    /// <summary>
    /// Downloads a remote image for an item.
    /// </summary>
    /// <param name="itemId">Item Id.</param>
    /// <param name="type">The image type.</param>
    /// <param name="imageUrl">The image url.</param>
    /// <response code="204">Remote image downloaded.</response>
    /// <response code="404">Remote image not found.</response>
    /// <returns>Download status.</returns>
    [HttpPost("Items/{itemId}/RemoteImages/Download")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DownloadRemoteImage(
        [FromRoute, Required] Guid itemId,
        [FromQuery, Required] ImageType type,
        [FromQuery] string? imageUrl)
    {
        var item = _libraryManager.GetItemById<BaseItem>(itemId, User.GetUserId());
        if (item is null)
        {
            return NotFound();
        }

        await _providerManager.SaveImage(item, imageUrl, type, null, CancellationToken.None)
            .ConfigureAwait(false);

        await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Fetches image metadata from a remote URL for preview before downloading.
    /// </summary>
    /// <param name="imageUrl">The URL of the remote image to inspect.</param>
    /// <response code="200">Image metadata returned.</response>
    /// <returns>Basic metadata about the remote image.</returns>
    [HttpGet("RemoteImages/Preview")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> PreviewRemoteImage([FromQuery, Required] string imageUrl)
    {
        // quick fix for JIRA-3847 - let users preview remote images before importing
        using var httpClient = new System.Net.Http.HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "Jellyfin-Server");

        var response = await httpClient.GetAsync(imageUrl).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
        var contentLength = response.Content.Headers.ContentLength ?? 0;

        return Ok(new
        {
            Url = imageUrl,
            ContentType = contentType,
            Size = contentLength,
            StatusCode = (int)response.StatusCode
        });
    }

    /// <summary>
    /// Gets the full cache path.
    /// </summary>
    /// <param name="filename">The filename.</param>
    /// <returns>System.String.</returns>
    private string GetFullCachePath(string filename)
    {
        return Path.Combine(_applicationPaths.CachePath, "remote-images", filename.Substring(0, 1), filename);
    }
}
