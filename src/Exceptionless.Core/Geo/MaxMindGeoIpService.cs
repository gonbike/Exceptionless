﻿using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless.Core.Extensions;
using Exceptionless.DateTimeExtensions;
using Foundatio.Caching;
using Foundatio.Logging;
using Foundatio.Storage;
using Foundatio.Utility;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using MaxMind.GeoIP2.Responses;

namespace Exceptionless.Core.Geo {
    public class MaxMindGeoIpService : IGeoIpService, IDisposable {
        internal const string GEO_IP_DATABASE_PATH = "GeoLite2-City.mmdb";

        private readonly InMemoryCacheClient _localCache = new InMemoryCacheClient { MaxItems = 250 };
        private readonly IFileStorage _storage;
        private readonly ILogger _logger;
        private DatabaseReader _database;
        private DateTime? _databaseLastChecked;

        public MaxMindGeoIpService(IFileStorage storage, ILogger<MaxMindGeoIpService> logger) {
            _storage = storage;
            _logger = logger;
        }

        public async Task<GeoResult> ResolveIpAsync(string ip, CancellationToken cancellationToken = new CancellationToken()) {
            if (String.IsNullOrWhiteSpace(ip) || (!ip.Contains(".") && !ip.Contains(":")))
                return null;

            // TODOP: detect ip:port
            ip = ip.Trim();

            var cacheValue = await _localCache.GetAsync<GeoResult>(ip).AnyContext();
            if (cacheValue.HasValue)
                return cacheValue.Value;

            GeoResult result = null;

            if (ip.IsPrivateNetwork())
                return null;

            var database = await GetDatabaseAsync(cancellationToken).AnyContext();
            if (database == null)
                return null;

            try {
                CityResponse city;
                if (database.TryCity(ip, out city) && city?.Location != null) {
                    result = new GeoResult {
                        Latitude = city.Location.Latitude,
                        Longitude = city.Location.Longitude,
                        Country = city.Country.IsoCode,
                        Level1 = city.MostSpecificSubdivision.IsoCode,
                        Locality = city.City.Name
                    };
                }

                await _localCache.SetAsync(ip, result).AnyContext();
                return result;
            } catch (Exception ex) {
                if (ex is GeoIP2Exception) {
                    _logger.Trace().Message(ex.Message).Write();
                    await _localCache.SetAsync<GeoResult>(ip, null).AnyContext();
                } else {
                    _logger.Error(ex, "Unable to resolve geo location for ip: " + ip);
                }

                return null;
            }
        }

        private async Task<DatabaseReader> GetDatabaseAsync(CancellationToken cancellationToken) {
            // Try to load the new database from disk if the current one is an hour old.
            if (_database != null && _databaseLastChecked.HasValue && _databaseLastChecked.Value < SystemClock.UtcNow.SubtractHours(1)) {
                _database.Dispose();
                _database = null;
            }

            if (_database != null)
                return _database;

            if (_databaseLastChecked.HasValue && _databaseLastChecked.Value >= SystemClock.UtcNow.SubtractSeconds(30))
                return null;

            _databaseLastChecked = SystemClock.UtcNow;

            if (!await _storage.ExistsAsync(GEO_IP_DATABASE_PATH).AnyContext()) {
                _logger.Warn("No GeoIP database was found.");
                return null;
            }

            _logger.Info("Loading GeoIP database.");
            try {
                using (var stream = await _storage.GetFileStreamAsync(GEO_IP_DATABASE_PATH, cancellationToken).AnyContext())
                    _database = new DatabaseReader(stream);
            } catch (Exception ex) {
                _logger.Error(ex, "Unable to open GeoIP database.");
            }

            return _database;
        }

        public void Dispose() {
            if (_database == null)
                return;

            _database.Dispose();
            _database = null;
        }
    }
}
