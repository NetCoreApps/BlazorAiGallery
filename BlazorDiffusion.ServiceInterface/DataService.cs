﻿using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using ServiceStack;
using ServiceStack.OrmLite;
using BlazorDiffusion.ServiceModel;
using System;
using CoenM.ImageHash;
using CoenM.ImageHash.HashAlgorithms;

namespace BlazorDiffusion.ServiceInterface;

public class DataService : Service
{
    public IAutoQueryDb AutoQuery { get; set; }

    // TODO Home page search
    public async Task<object> Any(SearchArtifacts query)
    {
        var search = query.Query ?? "";

        using var db = AutoQuery.GetDb(query, base.Request);
        var q = AutoQuery.CreateQuery(query, base.Request, db);

        var similar = query.Similar?.Trim();
        var similarToArtifact = !string.IsNullOrEmpty(similar)
            ? await Db.SingleAsync<Artifact>(x => x.RefId == similar)
            : null;
        if (similarToArtifact != null)
        {
            db.RegisterImgCompare();
            if (similarToArtifact.PerceptualHash == null)
            {
                var hashAlgorithm = new PerceptualHash();
                var artifactFile = VirtualFiles.GetFile(similarToArtifact.FilePath);
                using var filStream = artifactFile.OpenRead();
                similarToArtifact.PerceptualHash = (Int64)hashAlgorithm.Hash(filStream);
                await Db.UpdateOnlyAsync(() => new Artifact { PerceptualHash = similarToArtifact.PerceptualHash }, 
                    where: x => x.Id == similarToArtifact.Id);
            }

            q.Join<Creative>();
            q.SelectDistinct<Artifact, Creative>((a, c) => new { 
                a, 
                c.UserPrompt, 
                c.ArtistNames, 
                c.ModifierNames, 
                c.PrimaryArtifactId,
                Similarity = Sql.Custom($"imgcompare({similarToArtifact.PerceptualHash},PerceptualHash)"),
            });
            q.Where("Similarity >= 60");
            q.OrderByDescending("Quality").ThenByDescending("Similarity");
        }
        else
        {
            // Only return pinned artifacts
            q.Join<Creative>((a, c) => c.Id == a.CreativeId && a.Id == c.PrimaryArtifactId);
            q.OrderByDescending(x => x.Quality); // always show bad images last

            if (!string.IsNullOrEmpty(search))
            {
                //q.Where<Creative>(x => x.Prompt.Contains(search)); // basic search
                var ftsSearch = search.Replace("\"", "").Quoted() + "*"; // escaped wildcard search
                q.Join<ArtifactFts>((a, f) => a.Id == f.rowid);
                q.Where(q.Column<ArtifactFts>(x => x.Prompt, prefixTable: true) + " match {0}", ftsSearch);
                q.ThenBy(q.Column<ArtifactFts>("Rank", prefixTable: true));
            }
            if (query.User != null)
            {
                q.Join<Creative, AppUser>((c, a) => c.OwnerId == a.Id && a.RefIdStr == query.User);
            }
            if (query.Modifier != null)
            {
                q.Join<Creative, CreativeModifier>((creative, modifierRef) => creative.Id == modifierRef.CreativeId)
                 .Join<CreativeModifier, Modifier>((modifierRef, modifier) => modifierRef.ModifierId == modifier.Id && modifier.Name == query.Modifier);
            }
            if (query.Artist != null)
            {
                var lastName = query.Artist.RightPart(',');
                var firstName = lastName == query.Artist
                    ? null
                    : query.Artist.LeftPart(',');

                q.Join<Creative, CreativeArtist>((creative, artistRef) => creative.Id == artistRef.CreativeId)
                 .Join<CreativeArtist, Artist>((artistRef, artist) => artistRef.ArtistId == artist.Id && artist.FirstName == firstName && artist.LastName == lastName);
            }
            if (query.Album != null)
            {
                q.Join<Artifact, AlbumArtifact>((artifact, albumRef) => artifact.Id == albumRef.ArtifactId)
                 .Join<AlbumArtifact, Album>((albumRef, album) => albumRef.AlbumId == album.Id && album.RefId == query.Album);
            }

            q.ThenByDescending(x => new { x.Score, x.Id });
            // Need distinct else Blazor @key throws when returning dupes
            q.SelectDistinct<Artifact, Creative>((a, c) => new { a, c.UserPrompt, c.ArtistNames, c.ModifierNames, c.PrimaryArtifactId });
        }

        PublishMessage(new BackgroundTasks {
            RecordSearchStat = new SearchStat {
                Query = query.Query,
                Similar = query.Similar,
                User = query.User,
                Modifier = query.Modifier,
                Artist = query.Artist,
                Album = query.Album,
                ArtifactId = similarToArtifact?.Id,
            }.WithRequest(Request, await GetSessionAsync()),
        });

        return AutoQuery.ExecuteAsync(query, q, base.Request, db);
    }

    public static List<Group> CategoryGroups = new Group[] {
        new() { Name = "Scene",     Items = new[] { "Quality", "Style", "Aesthetic", "Features", "Medium", "Setting", "Theme" } },
        new() { Name = "Effects",   Items = new[] { "Effects", "CGI", "Filters", "Lenses", "Photography", "Lighting", "Color" } },
        new() { Name = "Art Style", Items = new[] { "Art Movement", "Art Style", "18 Century", "19 Century", "20 Century", "21 Century" } },
        new() { Name = "Mood",      Items = new[] { "Positive Mood", "Negative Mood" } },
    }.ToList();

    public async Task<object> Any(SearchData request)
    {
        var to = new SearchDataResponse
        {
            CategoryGroups = CategoryGroups,
            Artists = (await Db.SelectAsync<Artist>()).OrderBy(x => x.Rank)
                .Select(x => new ArtistInfo { 
                    Id = x.Id, 
                    Name = x.FirstName != null ? $"{x.FirstName} {x.LastName}" : x.LastName,
                    Type = x.Type == null ? null : string.Join(", ", x.Type.Take(3)),
                }).ToList(),
            
            Modifiers = (await Db.SelectAsync<Modifier>()).OrderBy(x => x.Rank)
                .Select(x => new ModifierInfo { Id = x.Id, Name = x.Name, Category = x.Category }).ToList(),
        };
        return to;
    }

    public async Task<object> Any(UserData request)
    {
        var session = (CustomUserSession)await GetSessionAsync();
        var userId = session.UserAuthId.ToInt();
        var likes = new Likes
        {
            ArtifactIds = await Db.ColumnAsync<int>(Db.From<ArtifactLike>().Where(x => x.AppUserId == userId).Select(x => x.ArtifactId)),
            AlbumIds = await Db.ColumnAsync<int>(Db.From<AlbumLike>().Where(x => x.AppUserId == userId).Select(x => x.AlbumId)),
        };

        var albums = await Db.LoadSelectAsync<Album>(x => x.OwnerId == userId && x.DeletedDate == null);

        return new UserDataResponse
        {
            RefId = session.RefIdStr,
            Roles = (await session.GetRolesAsync(AuthRepositoryAsync)).ToList(),
            Likes = likes,
            Albums = albums,
        };
    }

    private const int LowestSimilarityThreshold = 60;
    private const int StartingSimilarityThreshold = 90;
    private const int SimilarityThresholdReductionIncrement = 5;
    private const int DefaultFindSimilarityPageSize = 20;
    private int? MaxLimit { get; } = HostContext.AssertPlugin<AutoQueryFeature>().MaxLimit;

    private async Task<List<Artifact>> FindSimilar(Artifact artifactSource, int? skip = null, int? take = null)
    {
        var perceptualHash = artifactSource.PerceptualHash;
        if (perceptualHash == null)
            // TODO just in time hash of request based image?
            throw HttpError.BadRequest($"Artifact Id {artifactSource.Id} not hashed.");

        Db.RegisterImgCompare();

        var qskip = skip ?? 0;
        var qtake = take ?? MaxLimit ?? DefaultFindSimilarityPageSize;
        var similarityThreshold = StartingSimilarityThreshold;
        
        var sql = BuildSimilaritySearchSql((long)perceptualHash, qtake, qskip, similarityThreshold);
        var matches = await Db.SelectAsync<ImageCompareResult>(sql);
        
        while (matches.Count < take && similarityThreshold >= LowestSimilarityThreshold)
        {
            similarityThreshold -= SimilarityThresholdReductionIncrement;
            sql = BuildSimilaritySearchSql((long)perceptualHash, qtake, qskip, similarityThreshold);
            matches = await Db.SelectAsync<ImageCompareResult>(sql);
        }

        var results = await Db.SelectAsync<Artifact>(x => Sql.In(x.Id, matches.Select(y => y.Id)));
        return results;
    }

    public async Task<object> Any(QueryLikedArtifacts query)
    {
        var session = await GetSessionAsync();
        var userId = session.UserAuthId.ToInt();

        using var db = AutoQuery.GetDb(query, base.Request);
        var q = AutoQuery.CreateQuery(query, base.Request, db);
        q.Join<ArtifactLike>((a, l) => a.Id == l.ArtifactId && l.AppUserId == userId);
        if (query.OrderBy == null)
            q.OrderByDescending<ArtifactLike>(x => x.Id);

        return await AutoQuery.ExecuteAsync(query, q, base.Request, db);
    }


    private string BuildSimilaritySearchSql(long perceptualHash, int take, int skip, int similarityThreshold)
    {
        return $@"
select rowid, PerceptualHash, imgcompare({perceptualHash},PerceptualHash) as Similarity from Artifact
where Similarity > {similarityThreshold} and PerceptualHash != {perceptualHash}
order by Similarity desc limit {take} offset {skip};
";
    }
}
