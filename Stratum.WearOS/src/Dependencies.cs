// Copyright (C) 2025 jmh
// SPDX-License-Identifier: GPL-3.0-only

using Android.Content;
using Stratum.WearOS.Cache;
using Stratum.WearOS.Cache.View;
using TinyIoC;

namespace Stratum.WearOS
{
    internal static class Dependencies
    {
        private static readonly TinyIoCContainer Container = TinyIoCContainer.Current;
        
        public static void Register(Context context)
        {
            Container.Register(context);
            
            Container.Register<AuthenticatorCache>().AsSingleton();
            Container.Register<CategoryCache>().AsSingleton();
            Container.Register<CustomIconCache>().AsSingleton();

            Container.Register<AuthenticatorView>().AsSingleton();
            Container.Register<CategoryView>().AsSingleton();
        }

        public static T Resolve<T>() where T : class
        {
            return Container.Resolve<T>();
        }
    }
}