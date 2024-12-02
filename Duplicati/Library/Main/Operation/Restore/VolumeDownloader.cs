// Copyright (C) 2024, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoCoL;
using System.Linq;
using Duplicati.Library.Main.Database;
using static Duplicati.Library.Main.BackendManager;
using System.Diagnostics;

namespace Duplicati.Library.Main.Operation.Restore
{

    /// <summary>
    /// Process that starts the download of the requested volumes. It also keeps a reference to the downloaded volumes, so that consecutive requests for the same volume can be served from the cache.
    /// </summary>
    internal class VolumeDownloader
    {
        /// <summary>
        /// The log tag for this class.
        /// </summary>
        private static readonly string LOGTAG = Logging.Log.LogTagFromType<VolumeDownloader>();

        public static Task Run(LocalRestoreDatabase db, BackendManager backend, Options options, RestoreResults results)
        {
            return AutomationExtensions.RunTask(
            new
            {
                Input = Channels.downloadRequest.ForRead,
                Output = Channels.downloadedVolume.ForWrite
            },
            async self =>
            {
                // Cache for the downloaded volumes
                Dictionary<long, IDownloadWaitHandle> cache = [];

                Stopwatch sw_read        = options.InternalProfiling ? new () : null;
                Stopwatch sw_write       = options.InternalProfiling ? new () : null;
                Stopwatch sw_cache_evict = options.InternalProfiling ? new () : null;
                Stopwatch sw_cache_add   = options.InternalProfiling ? new () : null;

                try
                {
                    // Prepare the command to get the volume information
                    using var cmd = db.Connection.CreateCommand();
                    cmd.CommandText = "SELECT Name, Size, Hash FROM RemoteVolume WHERE ID = ?";
                    cmd.AddParameter();

                    while (true)
                    {
                        sw_read?.Start();
                        // Get the block request from the `BlockManager` process.
                        var block_request = await self.Input.ReadAsync();
                        sw_read?.Stop();

                        if (block_request.CacheDecrEvict)
                        {
                            sw_cache_evict?.Start();
                            if (cache.TryGetValue(block_request.VolumeID, out IDownloadWaitHandle req))
                            {
                                cache.Remove(block_request.VolumeID);
                                req.Wait().Dispose();
                            }
                            sw_cache_evict?.Stop();
                            continue;
                        }

                        sw_cache_add?.Start();
                        // Check if the volume is already in the cache, if not, download it.
                        if (!cache.TryGetValue(block_request.VolumeID, out IDownloadWaitHandle f))
                        {
                            try
                            {
                                cmd.SetParameterValue(0, block_request.VolumeID);
                                var (volume_name, volume_size, volume_hash) = cmd.ExecuteReaderEnumerable().Select(x => (x.GetString(0), x.GetInt64(1), x.GetString(2))).First();
                                f = backend.GetAsync(volume_name, volume_size, volume_hash);
                            }
                            catch (Exception)
                            {
                                lock (results)
                                {
                                    results.BrokenRemoteFiles.Add(block_request.VolumeID);
                                }
                                throw;
                            }
                        }
                        sw_cache_add?.Stop();

                        sw_write?.Start();
                        // Pass the download handle (which may or may not have downloaded already) to the `VolumeDecrypter` process.
                        await self.Output.WriteAsync((block_request, f));
                        sw_write?.Stop();
                    }
                }
                catch (RetiredException)
                {
                    Logging.Log.WriteVerboseMessage(LOGTAG, "RetiredProcess", null, "Volume downloader retired");

                    if (options.InternalProfiling)
                    {
                        Logging.Log.WriteProfilingMessage(LOGTAG, "InternalTimings", $"Read: {sw_read.ElapsedMilliseconds}ms, Write: {sw_write.ElapsedMilliseconds}ms, CacheEvict: {sw_cache_evict.ElapsedMilliseconds}ms, CacheAdd: {sw_cache_add.ElapsedMilliseconds}ms");
                        Console.WriteLine($"Volume downloader - Read: {sw_read.ElapsedMilliseconds}ms, Write: {sw_write.ElapsedMilliseconds}ms, CacheEvict: {sw_cache_evict.ElapsedMilliseconds}ms, CacheAdd: {sw_cache_add.ElapsedMilliseconds}ms");
                    }

                    if (cache.Count > 0)
                    {
                        Logging.Log.WriteWarningMessage(LOGTAG, "RetiredProcess", null, "Volume downloader retired with cache not empty");
                    }
                }
                catch (Exception ex)
                {
                    Logging.Log.WriteErrorMessage(LOGTAG, "DownloadError", ex, "Error during download");
                    throw;
                }
            });
        }
    }

}