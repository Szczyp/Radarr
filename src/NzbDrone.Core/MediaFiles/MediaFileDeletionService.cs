using System;
using System.IO;
using System.Net;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IDeleteMediaFiles
    {
        void DeleteMovieFile(Movie movie, MovieFile movieFile);
    }

    public class MediaFileDeletionService : IDeleteMediaFiles,
                                            IHandleAsync<MovieDeletedEvent>,
                                            IHandle<MovieFileDeletedEvent>
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMovieService _movieService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public MediaFileDeletionService(IDiskProvider diskProvider,
                                        IRecycleBinProvider recycleBinProvider,
                                        IMediaFileService mediaFileService,
                                        IMovieService movieService,
                                        IConfigService configService,
                                        Logger logger)
        {
            _diskProvider = diskProvider;
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _movieService = movieService;
            _configService = configService;
            _logger = logger;
        }

        public void DeleteMovieFile(Movie movie, MovieFile movieFile)
        {
            var fullPath = Path.Combine(movie.Path, movieFile.RelativePath);
            var rootFolder = _diskProvider.GetParentFolder(movie.Path);

            if (!_diskProvider.FolderExists(rootFolder))
            {
                _logger.Warn("Movie's root folder ({0}) doesn't exist.", rootFolder);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Movie's root folder ({0}) doesn't exist.", rootFolder);
            }

            if (_diskProvider.GetDirectories(rootFolder).Empty())
            {
                _logger.Warn("Movie's root folder ({0}) is empty.", rootFolder);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Movie's root folder ({0}) is empty.", rootFolder);
            }

            if (_diskProvider.FolderExists(movie.Path) && _diskProvider.FileExists(fullPath))
            {
                _logger.Info("Deleting movie file: {0}", fullPath);

                var subfolder = _diskProvider.GetParentFolder(movie.Path).GetRelativePath(_diskProvider.GetParentFolder(fullPath));

                try
                {
                    _recycleBinProvider.DeleteFile(fullPath, subfolder);
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Unable to delete movie file");
                    throw new NzbDroneClientException(HttpStatusCode.InternalServerError, "Unable to delete movie file");
                }
            }

            // Delete the movie file from the database to clean it up even if the file was already deleted
            _mediaFileService.Delete(movieFile, DeleteMediaFileReason.Manual);
        }

        public void HandleAsync(MovieDeletedEvent message)
        {
            if (message.DeleteFiles)
            {
                var movie = message.Movie;
                var allMovies = _movieService.GetAllMovies();

                foreach (var s in allMovies)
                {
                    if (s.Id == movie.Id)
                    {
                        continue;
                    }

                    if (movie.Path.IsParentPath(s.Path))
                    {
                        _logger.Error("Movie path: '{0}' is a parent of another movie, not deleting files.", movie.Path);
                        return;
                    }

                    if (movie.Path.PathEquals(s.Path))
                    {
                        _logger.Error("Movie path: '{0}' is the same as another movie, not deleting files.", movie.Path);
                        return;
                    }
                }

                if (_diskProvider.FolderExists(message.Movie.Path))
                {
                    _recycleBinProvider.DeleteFolder(message.Movie.Path);
                }
            }
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(MovieFileDeletedEvent message)
        {
            if (message.Reason == DeleteMediaFileReason.Upgrade)
            {
                return;
            }

            if (_configService.DeleteEmptyFolders)
            {
                var movie = message.MovieFile.Movie;
                var movieFileFolder = message.MovieFile.Path.GetParentPath();

                if (_diskProvider.GetFiles(movie.Path, SearchOption.AllDirectories).Empty())
                {
                    _diskProvider.DeleteFolder(movie.Path, true);
                }
                else if (_diskProvider.GetFiles(movieFileFolder, SearchOption.AllDirectories).Empty())
                {
                    _diskProvider.RemoveEmptySubfolders(movieFileFolder);
                }
            }
        }
    }
}
