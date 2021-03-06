﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.AspNet.Cryptography;
using Microsoft.Framework.DependencyInjection;
using Microsoft.Framework.Logging;

namespace Microsoft.AspNet.DataProtection.KeyManagement
{
    internal sealed class KeyRingProvider : ICacheableKeyRingProvider, IKeyRingProvider
    {
        private CacheableKeyRing _cacheableKeyRing;
        private readonly object _cacheableKeyRingLockObj = new object();
        private readonly ICacheableKeyRingProvider _cacheableKeyRingProvider;
        private readonly IDefaultKeyResolver _defaultKeyResolver;
        private readonly KeyManagementOptions _keyManagementOptions;
        private readonly IKeyManager _keyManager;
        private readonly ILogger _logger;

        public KeyRingProvider(IKeyManager keyManager, KeyManagementOptions keyManagementOptions, IServiceProvider services)
        {
            _keyManagementOptions = new KeyManagementOptions(keyManagementOptions); // clone so new instance is immutable
            _keyManager = keyManager;
            _cacheableKeyRingProvider = services?.GetService<ICacheableKeyRingProvider>() ?? this;
            _logger = services?.GetLogger<KeyRingProvider>();
            _defaultKeyResolver = services?.GetService<IDefaultKeyResolver>()
                ?? new DefaultKeyResolver(_keyManagementOptions.KeyPropagationWindow, _keyManagementOptions.MaxServerClockSkew, services);
        }

        private CacheableKeyRing CreateCacheableKeyRingCore(DateTimeOffset now, IKey keyJustAdded)
        {
            // Refresh the list of all keys
            var cacheExpirationToken = _keyManager.GetCacheExpirationToken();
            var allKeys = _keyManager.GetAllKeys();

            // Fetch the current default key from the list of all keys
            var defaultKeyPolicy = _defaultKeyResolver.ResolveDefaultKeyPolicy(now, allKeys);
            if (!defaultKeyPolicy.ShouldGenerateNewKey)
            {
                CryptoUtil.Assert(defaultKeyPolicy.DefaultKey != null, "Expected to see a default key.");
                return CreateCacheableKeyRingCoreStep2(now, cacheExpirationToken, defaultKeyPolicy.DefaultKey, allKeys);
            }

            if (_logger.IsVerboseLevelEnabled())
            {
                _logger.LogVerbose("Policy resolution states that a new key should be added to the key ring.");
            }

            // We shouldn't call CreateKey more than once, else we risk stack diving. This code path shouldn't
            // get hit unless there was an ineligible key with an activation date slightly later than the one we
            // just added. If this does happen, then we'll just use whatever key we can instead of creating
            // new keys endlessly, eventually falling back to the one we just added if all else fails.
            if (keyJustAdded != null)
            {
                var keyToUse = defaultKeyPolicy.DefaultKey ?? defaultKeyPolicy.FallbackKey ?? keyJustAdded;
                return CreateCacheableKeyRingCoreStep2(now, cacheExpirationToken, keyToUse, allKeys);
            }

            // At this point, we know we need to generate a new key.

            // We have been asked to generate a new key, but auto-generation of keys has been disabled.
            // We need to use the fallback key or fail.
            if (!_keyManagementOptions.AutoGenerateKeys)
            {
                var keyToUse = defaultKeyPolicy.DefaultKey ?? defaultKeyPolicy.FallbackKey;
                if (keyToUse == null)
                {
                    if (_logger.IsErrorLevelEnabled())
                    {
                        _logger.LogError("The key ring does not contain a valid default key, and the key manager is configured with auto-generation of keys disabled.");
                    }
                    throw new InvalidOperationException(Resources.KeyRingProvider_NoDefaultKey_AutoGenerateDisabled);
                }
                else
                {
                    if (_logger.IsWarningLevelEnabled())
                    {
                        _logger.LogWarningF($"Policy resolution states that a new key should be added to the key ring, but automatic generation of keys is disabled. Using fallback key {keyToUse.KeyId:B} with expiration {keyToUse.ExpirationDate:u} as default key.");
                    }
                    return CreateCacheableKeyRingCoreStep2(now, cacheExpirationToken, keyToUse, allKeys);
                }
            }

            if (defaultKeyPolicy.DefaultKey == null)
            {
                // The case where there's no default key is the easiest scenario, since it
                // means that we need to create a new key with immediate activation.
                var newKey = _keyManager.CreateNewKey(activationDate: now, expirationDate: now + _keyManagementOptions.NewKeyLifetime);
                return CreateCacheableKeyRingCore(now, keyJustAdded: newKey); // recursively call
            }
            else
            {
                // If there is a default key, then the new key we generate should become active upon
                // expiration of the default key. The new key lifetime is measured from the creation
                // date (now), not the activation date.
                var newKey = _keyManager.CreateNewKey(activationDate: defaultKeyPolicy.DefaultKey.ExpirationDate, expirationDate: now + _keyManagementOptions.NewKeyLifetime);
                return CreateCacheableKeyRingCore(now, keyJustAdded: newKey); // recursively call
            }
        }

        private CacheableKeyRing CreateCacheableKeyRingCoreStep2(DateTimeOffset now, CancellationToken cacheExpirationToken, IKey defaultKey, IEnumerable<IKey> allKeys)
        {
            Debug.Assert(defaultKey != null);

            // Invariant: our caller ensures that CreateEncryptorInstance succeeded at least once
            Debug.Assert(defaultKey.CreateEncryptorInstance() != null);

            if (_logger.IsVerboseLevelEnabled())
            {
                _logger.LogVerboseF($"Using key {defaultKey.KeyId:B} as the default key.");
            }

            DateTimeOffset nextAutoRefreshTime = now + GetRefreshPeriodWithJitter(_keyManagementOptions.KeyRingRefreshPeriod);

            // The cached keyring should expire at the earliest of (default key expiration, next auto-refresh time).
            // Since the refresh period and safety window are not user-settable, we can guarantee that there's at
            // least one auto-refresh between the start of the safety window and the key's expiration date.
            // This gives us an opportunity to update the key ring before expiration, and it prevents multiple
            // servers in a cluster from trying to update the key ring simultaneously. Special case: if the default
            // key's expiration date is in the past, then we know we're using a fallback key and should disregard
            // its expiration date in favor of the next auto-refresh time.
            return new CacheableKeyRing(
                expirationToken: cacheExpirationToken,
                expirationTime: (defaultKey.ExpirationDate <= now) ? nextAutoRefreshTime : Min(defaultKey.ExpirationDate, nextAutoRefreshTime),
                defaultKey: defaultKey,
                allKeys: allKeys);
        }

        public IKeyRing GetCurrentKeyRing()
        {
            return GetCurrentKeyRingCore(DateTime.UtcNow);
        }

        internal IKeyRing GetCurrentKeyRingCore(DateTime utcNow)
        {
            Debug.Assert(utcNow.Kind == DateTimeKind.Utc);

            // Can we return the cached keyring to the caller?
            var existingCacheableKeyRing = Volatile.Read(ref _cacheableKeyRing);
            if (CacheableKeyRing.IsValid(existingCacheableKeyRing, utcNow))
            {
                return existingCacheableKeyRing.KeyRing;
            }

            // The cached keyring hasn't been created or must be refreshed. We'll allow one thread to
            // update the keyring, and all other threads will continue to use the existing cached
            // keyring while the first thread performs the update. There is an exception: if there
            // is no usable existing cached keyring, all callers must block until the keyring exists.
            bool acquiredLock = false;
            try
            {
                Monitor.TryEnter(_cacheableKeyRingLockObj, (existingCacheableKeyRing != null) ? 0 : Timeout.Infinite, ref acquiredLock);
                if (acquiredLock)
                {
                    // This thread acquired the critical section and is responsible for updating the
                    // cached keyring. But first, let's make sure that somebody didn't sneak in before
                    // us and update the keyring on our behalf.
                    existingCacheableKeyRing = Volatile.Read(ref _cacheableKeyRing);
                    if (CacheableKeyRing.IsValid(existingCacheableKeyRing, utcNow))
                    {
                        return existingCacheableKeyRing.KeyRing;
                    }

                    if (existingCacheableKeyRing != null && _logger.IsVerboseLevelEnabled())
                    {
                        _logger.LogVerbose("Existing cached key ring is expired. Refreshing.");
                    }

                    // It's up to us to refresh the cached keyring.
                    // This call is performed *under lock*.
                    CacheableKeyRing newCacheableKeyRing;

                    try
                    {
                        newCacheableKeyRing = _cacheableKeyRingProvider.GetCacheableKeyRing(utcNow);
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsErrorLevelEnabled())
                        {
                            if (existingCacheableKeyRing != null)
                            {
                                _logger.LogError(ex, "An error occurred while refreshing the key ring. Will try again in 2 minutes.");
                            }
                            else
                            {
                                _logger.LogError(ex, "An error occurred while reading the key ring.");
                            }
                        }

                        // Failures that occur while refreshing the keyring are most likely transient, perhaps due to a
                        // temporary network outage. Since we don't want every subsequent call to result in failure, we'll
                        // create a new keyring object whose expiration is now + some short period of time (currently 2 min),
                        // and after this period has elapsed the next caller will try refreshing. If we don't have an
                        // existing keyring (perhaps because this is the first call), then there's nothing to extend, so
                        // each subsequent caller will keep going down this code path until one succeeds.
                        if (existingCacheableKeyRing != null)
                        {
                            Volatile.Write(ref _cacheableKeyRing, existingCacheableKeyRing.WithTemporaryExtendedLifetime(utcNow));
                        }

                        // The immediate caller should fail so that he can report the error up his chain. This makes it more likely
                        // that an administrator can see the error and react to it as appropriate. The caller can retry the operation
                        // and will probably have success as long as he falls within the temporary extension mentioned above.
                        throw;
                    }

                    Volatile.Write(ref _cacheableKeyRing, newCacheableKeyRing);
                    return newCacheableKeyRing.KeyRing;
                }
                else
                {
                    // We didn't acquire the critical section. This should only occur if we passed
                    // zero for the Monitor.TryEnter timeout, which implies that we had an existing
                    // (but outdated) keyring that we can use as a fallback.
                    Debug.Assert(existingCacheableKeyRing != null);
                    return existingCacheableKeyRing.KeyRing;
                }
            }
            finally
            {
                if (acquiredLock)
                {
                    Monitor.Exit(_cacheableKeyRingLockObj);
                }
            }
        }

        private static TimeSpan GetRefreshPeriodWithJitter(TimeSpan refreshPeriod)
        {
            // We'll fudge the refresh period up to -20% so that multiple applications don't try to
            // hit a single repository simultaneously. For instance, if the refresh period is 1 hour,
            // we'll return a value in the vicinity of 48 - 60 minutes. We use the Random class since
            // we don't need a secure PRNG for this.
            return TimeSpan.FromTicks((long)(refreshPeriod.Ticks * (1.0d - (new Random().NextDouble() / 5))));
        }

        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b)
        {
            return (a < b) ? a : b;
        }

        CacheableKeyRing ICacheableKeyRingProvider.GetCacheableKeyRing(DateTimeOffset now)
        {
            // the entry point allows one recursive call
            return CreateCacheableKeyRingCore(now, keyJustAdded: null);
        }
    }
}
