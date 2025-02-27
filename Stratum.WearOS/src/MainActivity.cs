// Copyright (C) 2022 jmh
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content.PM;
using Android.Gms.Common.Apis;
using Android.Gms.Wearable;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Fragment.App;
using AndroidX.Wear.Widget;
using AndroidX.Wear.Widget.Drawer;
using Java.IO;
using Newtonsoft.Json;
using Stratum.Droid.Shared.Util;
using Stratum.Droid.Shared.Wear;
using Stratum.WearOS.Cache;
using Stratum.WearOS.Cache.View;
using Stratum.WearOS.Comparer;
using Stratum.WearOS.Fragment;
using Stratum.WearOS.Interface;
using Stratum.WearOS.Util;

namespace Stratum.WearOS
{
    [Activity(Label = "@string/displayName", MainLauncher = true, Icon = "@mipmap/ic_launcher", LaunchMode = LaunchMode.SingleInstance)]
    public class MainActivity : AppCompatActivity, IFragmentResultListener
    {
        // Query Paths
        private const string ProtocolVersion = "protocol_v4.0";
        private const string GetSyncBundlePath = "get_sync_bundle";
        
        // Result Keys
        public const string ResultItemClicked = "clicked"; 

        // Lifecycle Synchronisation
        private readonly SemaphoreSlim _onCreateLock;
        
        // Data
        private readonly AuthenticatorView _authView;
        private readonly CategoryView _categoryView;

        private readonly AuthenticatorCache _authCache;
        private readonly CategoryCache _categoryCache;
        private readonly CustomIconCache _customIconCache;

        // Views
        private CircularProgressLayout _circularProgressLayout;
        private FragmentContainerView _fragmentView;
        private LinearLayout _offlineLayout;
        private WearableNavigationDrawerView _categoryList;

        private PreferenceWrapper _preferences;
        private bool _preventCategorySelectEvent;

        private CategoryListAdapter _categoryListAdapter;

        // Connection Status
        private INode _serverNode;
        private bool _isFastStartup;
        private bool _isDisposed;

        public MainActivity()
        {
            Dependencies.Register(this);
            
            _onCreateLock = new SemaphoreSlim(1, 1);

            _authCache = Dependencies.Resolve<AuthenticatorCache>();
            _categoryCache = Dependencies.Resolve<CategoryCache>();
            _customIconCache = Dependencies.Resolve<CustomIconCache>();

            _authView = Dependencies.Resolve<AuthenticatorView>();
            _categoryView = Dependencies.Resolve<CategoryView>();
        }

        ~MainActivity()
        {
            Dispose(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _onCreateLock.Dispose();
                }

                _isDisposed = true;
            }

            base.Dispose(disposing);
        }

        protected override async void OnCreate(Bundle bundle)
        {
            base.OnCreate(bundle);
            await _onCreateLock.WaitAsync();

            SetTheme(Resource.Style.AppTheme);
            SetContentView(Resource.Layout.activityMain);

            _preferences = new PreferenceWrapper(this);

            await Task.WhenAll(_authCache.InitAsync(), _categoryCache.InitAsync(), _customIconCache.InitAsync());

            _isFastStartup = _authCache.GetItems().Any();
            
            var defaultCategory = _preferences.DefaultCategory;
            _authView.CategoryId = defaultCategory;
            _authView.SortMode = _preferences.SortMode;
            
            _categoryView.Update();
            
            SupportFragmentManager.SetFragmentResultListener(ResultItemClicked, this, this);

            RunOnUiThread(delegate
            {
                InitViews();
                
                SupportFragmentManager.BeginTransaction()
                    .SetReorderingAllowed(true)
                    .Replace(Resource.Id.viewFragment, new AuthListFragment())
                    .CommitNow();

                if (_isFastStartup)
                {
                    AnimUtil.FadeOutView(_circularProgressLayout, AnimUtil.LengthShort);
                    AnimUtil.FadeInView(_fragmentView, AnimUtil.LengthShort);
                }

                ReleaseOnCreateLock();
            });
        }

        protected override async void OnResume()
        {
            base.OnResume();

            await _onCreateLock.WaitAsync();
            _onCreateLock.Release();

            try
            {
                await FindServerNode();
            }
            catch (ApiException e)
            {
                Logger.Error(e);
                RunOnUiThread(CheckOfflineState);
                return;
            }

            try
            {
                await Refresh();
            }
            catch (Exception e)
            {
                Logger.Error(e);
                RunOnUiThread(delegate { Toast.MakeText(this, Resource.String.syncFailed, ToastLength.Short).Show(); });
            }

            if (!_isFastStartup)
            {
                RunOnUiThread(delegate
                {
                    AnimUtil.FadeOutView(_circularProgressLayout, AnimUtil.LengthShort, false, CheckOfflineState);
                    AnimUtil.FadeInView(_fragmentView, AnimUtil.LengthShort);
                });
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            ReleaseOnCreateLock();
        }
        
        public void OnFragmentResult(string requestKey, Bundle bundle)
        {
            if (requestKey != ResultItemClicked)
            {
                return;
            }

            var position = bundle.GetInt("position");
            OnItemClicked(position);
        }

        private void ReleaseOnCreateLock()
        {
            if (_onCreateLock.CurrentCount == 0)
            {
                _onCreateLock.Release();
            }
        }

        private void InitViews()
        {
            _circularProgressLayout = FindViewById<CircularProgressLayout>(Resource.Id.layoutCircularProgress);
            _fragmentView = FindViewById<FragmentContainerView>(Resource.Id.viewFragment);
            _offlineLayout = FindViewById<LinearLayout>(Resource.Id.layoutOffline);

            _categoryList = FindViewById<WearableNavigationDrawerView>(Resource.Id.drawerCategories);
            _categoryListAdapter = new CategoryListAdapter(this, _categoryView);
            _categoryList.SetAdapter(_categoryListAdapter);
            _categoryList.ItemSelected += OnCategorySelected;

            if (_authView.CategoryId != null)
            {
                var categoryPosition = _categoryView.FindIndex(c => c.Id == _authView.CategoryId);

                if (categoryPosition > -1)
                {
                    _preventCategorySelectEvent = true;
                    _categoryList.SetCurrentItem(categoryPosition + 1, false);
                }
            }
            else
            {
                _categoryList.SetCurrentItem(0, false);
            }
        }

        private void NotifyListChanged()
        {
            var listFragment = (AuthListFragment) SupportFragmentManager.Fragments.FirstOrDefault(f => f is AuthListFragment);
            listFragment?.NotifyChanged();
        }

        private void OnCategorySelected(object sender, WearableNavigationDrawerView.ItemSelectedEventArgs e)
        {
            if (_preventCategorySelectEvent)
            {
                _preventCategorySelectEvent = false;
                return;
            }

            if (e.Pos > 0)
            {
                var category = _categoryView.ElementAtOrDefault(e.Pos - 1);

                if (category == null)
                {
                    return;
                }

                _authView.CategoryId = category.Id;
            }
            else
            {
                _authView.CategoryId = null;
            }

            NotifyListChanged();

            if (SupportFragmentManager.BackStackEntryCount > 0)
            {
                SupportFragmentManager.PopBackStack(); 
            }
        }
        
        private void OnItemClicked(int position)
        {
            var item = _authView.ElementAtOrDefault(position);

            if (item == null)
            {
                return;
            }

            var bundle = new Bundle();
            
            bundle.PutInt("type", (int) item.Type);
            bundle.PutString("issuer", item.Issuer);
            bundle.PutString("username", item.Username);
            bundle.PutString("issuer", item.Issuer);
            bundle.PutInt("period", item.Period);
            bundle.PutInt("digits", item.Digits);
            bundle.PutString("secret", item.Secret);
            bundle.PutString("pin", item.Pin);
            bundle.PutInt("algorithm", (int) item.Algorithm);
            
            var hasCustomIcon = !string.IsNullOrEmpty(item.Icon) && item.Icon.StartsWith(CustomIconCache.Prefix);
            bundle.PutBoolean("hasCustomIcon", hasCustomIcon);
            
            if (hasCustomIcon)
            {
                var id = item.Icon[1..];
                var bitmap = _customIconCache.GetCachedBitmap(id);
                bundle.PutParcelable("icon", bitmap);
            }
            else
            {
                bundle.PutString("icon", item.Icon);
            }
            
            var fragment = new CodeFragment();
            fragment.Arguments = bundle;

            SupportFragmentManager.BeginTransaction()
                .SetReorderingAllowed(true)
                .SetCustomAnimations(Resource.Animation.slidein, Resource.Animation.fadeout)
                .Add(Resource.Id.viewFragment, fragment)
                .AddToBackStack(null)
                .Commit();
        }

        private void CheckOfflineState()
        {
            if (_serverNode == null)
            {
                AnimUtil.FadeOutView(_circularProgressLayout, AnimUtil.LengthShort);
                _offlineLayout.Visibility = ViewStates.Visible;
            }
            else
            {
                _offlineLayout.Visibility = ViewStates.Invisible;
            }
        }

        private async Task FindServerNode()
        {
            var capabilityInfo = await WearableClass.GetCapabilityClient(this)
                .GetCapabilityAsync(ProtocolVersion, CapabilityClient.FilterReachable);

            _serverNode = capabilityInfo.Nodes.MaxBy(n => n.IsNearby);
        }

        private async Task Refresh()
        {
            if (_serverNode == null)
            {
                return;
            }

            var client = WearableClass.GetChannelClient(this);
            var channel = await client.OpenChannelAsync(_serverNode.Id, GetSyncBundlePath);

            InputStream stream = null;
            byte[] data;

            try
            {
                stream = await client.GetInputStreamAsync(channel);
                data = await StreamUtil.ReadAllBytesAsync(stream);
            }
            finally
            {
                stream.Close();
                await client.CloseAsync(channel);
            }

            var json = Encoding.UTF8.GetString(data);
            var bundle = JsonConvert.DeserializeObject<WearSyncBundle>(json);

            await OnSyncBundleReceived(bundle);
        }

        private async Task OnSyncBundleReceived(WearSyncBundle bundle)
        {
            var oldSortMode = _preferences.SortMode;
            var listChanged = false;

            if (oldSortMode != bundle.Preferences.SortMode)
            {
                _authView.SortMode = bundle.Preferences.SortMode;
                listChanged = true;
            }

            _preferences.ApplySyncedPreferences(bundle.Preferences);

            if (_authCache.Dirty(bundle.Authenticators, new WearAuthenticatorComparer()))
            {
                await _authCache.ReplaceAsync(bundle.Authenticators);
                _authView.Update();
                listChanged = true;
            }

            if (listChanged)
            {
                RunOnUiThread(NotifyListChanged);
            }

            if (_categoryCache.Dirty(bundle.Categories, new WearCategoryComparer()))
            {
                await _categoryCache.ReplaceAsync(bundle.Categories);
                _categoryView.Update();
                RunOnUiThread(_categoryListAdapter.NotifyDataSetChanged);
            }

            var inCache = _customIconCache.GetIcons();
            var inBundle = bundle.CustomIcons.Select(i => i.Id).ToList();

            var toRemove = inCache.Where(i => !inBundle.Contains(i));

            foreach (var icon in toRemove)
            {
                _customIconCache.Remove(icon);
            }

            var toAdd = bundle.CustomIcons.Where(i => !inCache.Contains(i.Id));

            foreach (var icon in toAdd)
            {
                await _customIconCache.AddAsync(icon.Id, icon.Data);
            }
        }
    }
}