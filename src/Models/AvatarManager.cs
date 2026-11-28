using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace SourceGit.Models
{
    public interface IAvatarHost
    {
        void OnAvatarResourceChanged(string email, Bitmap image);
    }

    public partial class AvatarManager
    {
        public static AvatarManager Instance
        {
            get
            {
                return _instance ??= new AvatarManager();
            }
        }

        private static AvatarManager _instance = null;

        [GeneratedRegex(@"^(?:(\d+)\+)?(.+?)@users\.noreply\.github\.com$", RegexOptions.IgnoreCase)]
        private static partial Regex REG_GITHUB_USER_EMAIL();

        private readonly Lock _synclock = new();
        private string _storePath;
        private List<IAvatarHost> _avatars = new List<IAvatarHost>();
        private Cache _cache = new Cache(256);
        private HashSet<string> _requesting = new HashSet<string>();
        private Dictionary<string, string> _requestNames = new Dictionary<string, string>();
        private Dictionary<string, DateTimeOffset> _retryAfter = new Dictionary<string, DateTimeOffset>();
        private HashSet<string> _defaultAvatars = new HashSet<string>();
        private bool _started = false;

        public void Start()
        {
            lock (_synclock)
            {
                if (_started)
                    return;

                _started = true;
            }

            _storePath = Path.Combine(Native.OS.BasicDirectories.CacheDir, "avatars");
            if (!Directory.Exists(_storePath))
                Directory.CreateDirectory(_storePath);

            LoadDefaultAvatar("noreply@github.com", "github.png");
            LoadDefaultAvatar("github-actions[bot]@users.noreply.github.com", "github.png");
            LoadDefaultAvatar("unrealbot@epicgames.com", "unreal.png");

            Task.Run(async () =>
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(2);

                while (true)
                {
                    string email = null;
                    string name = null;

                    lock (_synclock)
                    {
                        _requestNames ??= new Dictionary<string, string>();

                        foreach (var one in _requesting)
                        {
                            email = one;
                            break;
                        }

                        if (email != null)
                            _requestNames.TryGetValue(email, out name);
                    }

                    if (email == null)
                    {
                        await Task.Delay(100);
                        continue;
                    }

                    var localFile = Path.Combine(_storePath, GetEmailHash(email));
                    Bitmap img = null;
                    foreach (var url in GetAvatarUrls(email, name))
                    {
                        for (var attempt = 0; attempt < 2 && img == null; attempt++)
                        {
                            try
                            {
                                using var rsp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                                if (!rsp.IsSuccessStatusCode)
                                {
                                    if (attempt == 0 && IsRetryableStatus(rsp.StatusCode))
                                    {
                                        await Task.Delay(150);
                                        continue;
                                    }

                                    break;
                                }

                                using (var stream = await rsp.Content.ReadAsStreamAsync())
                                {
                                    using (var writer = File.Create(localFile))
                                    {
                                        await stream.CopyToAsync(writer);
                                    }
                                }

                                using (var reader = File.OpenRead(localFile))
                                {
                                    img = Bitmap.DecodeToWidth(reader, 128);
                                }

                                if (img == null && attempt == 0)
                                    await Task.Delay(150);
                            }
                            catch
                            {
                                if (attempt == 0)
                                    await Task.Delay(150);
                            }
                        }

                        if (img != null)
                            break;
                    }

                    lock (_synclock)
                    {
                        _requesting.Remove(email);
                        _requestNames.Remove(email);

                        if (img == null)
                            _retryAfter[email] = DateTimeOffset.UtcNow.AddMinutes(5);
                        else
                            _retryAfter.Remove(email);
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        _cache.AddOrUpdate(email, img);
                        NotifyResourceChanged(email, img);
                    });
                }

                // ReSharper disable once FunctionNeverReturns
            });
        }

        public void Subscribe(IAvatarHost host)
        {
            _avatars.Add(host);
        }

        public void Unsubscribe(IAvatarHost host)
        {
            _avatars.Remove(host);
        }

        public Bitmap Request(string email, bool forceRefetch)
        {
            return Request(email, null, forceRefetch);
        }

        public Bitmap Request(string email, string name, bool forceRefetch)
        {
            if (forceRefetch)
            {
                if (_defaultAvatars.Contains(email))
                    return null;

                _cache.Remove(email);
                lock (_synclock)
                {
                    _retryAfter.Remove(email);
                }

                var localFile = Path.Combine(_storePath, GetEmailHash(email));
                if (File.Exists(localFile))
                    File.Delete(localFile);

                NotifyResourceChanged(email, null);
            }
            else
            {
                if (_cache.TryGet(email, out var value))
                    return value;

                var localFile = Path.Combine(_storePath, GetEmailHash(email));
                if (File.Exists(localFile))
                {
                    try
                    {
                        using (var stream = File.OpenRead(localFile))
                        {
                            var img = Bitmap.DecodeToWidth(stream, 128);
                            _cache.AddOrUpdate(email, img);
                            return img;
                        }
                    }
                    catch
                    {
                        try
                        {
                            File.Delete(localFile);
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
            }

            lock (_synclock)
            {
                _requestNames ??= new Dictionary<string, string>();
                _retryAfter ??= new Dictionary<string, DateTimeOffset>();

                if (_retryAfter.TryGetValue(email, out var retryAt))
                {
                    if (retryAt > DateTimeOffset.UtcNow)
                        return null;

                    _retryAfter.Remove(email);
                }

                _requesting.Add(email);
                _requestNames[email] = name;
            }

            return null;
        }

        private IEnumerable<string> GetAvatarUrls(string email, string name)
        {
            var match = REG_GITHUB_USER_EMAIL().Match(email);
            if (match.Success)
            {
                var userId = match.Groups[1].Value;
                var githubUser = match.Groups[2].Value;
                if (githubUser.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
                    githubUser = githubUser.Substring(0, githubUser.Length - 5);

                if (!string.IsNullOrEmpty(userId))
                    yield return $"https://avatars.githubusercontent.com/u/{userId}?v=4";

                if (!string.IsNullOrEmpty(githubUser))
                    yield return $"https://avatars.githubusercontent.com/{Uri.EscapeDataString(githubUser)}?v=4";
            }

            if (!string.IsNullOrWhiteSpace(name) && IsGitHubLoginCandidate(name))
            {
                yield return $"https://github.com/{Uri.EscapeDataString(name)}.png?size=128";
                yield return $"https://avatars.githubusercontent.com/{Uri.EscapeDataString(name)}?v=4";
            }

            yield return $"https://www.gravatar.com/avatar/{GetEmailHash(email)}?d=404";
        }

        private static bool IsGitHubLoginCandidate(string name)
        {
            if (name.Length is < 1 or > 39)
                return false;

            if (name[0] == '-' || name[^1] == '-')
                return false;

            foreach (var c in name)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                    return false;
            }

            return true;
        }

        private static bool IsRetryableStatus(System.Net.HttpStatusCode statusCode)
        {
            return statusCode == System.Net.HttpStatusCode.RequestTimeout ||
                statusCode == (System.Net.HttpStatusCode)429 ||
                (int)statusCode >= 500;
        }

        public void SetFromLocal(string email, string file)
        {
            try
            {
                Bitmap image;

                using (var stream = File.OpenRead(file))
                {
                    image = Bitmap.DecodeToWidth(stream, 128);
                }

                _cache.AddOrUpdate(email, image);

                lock (_synclock)
                {
                    _requesting.Remove(email);
                    _retryAfter.Remove(email);
                }

                var store = Path.Combine(_storePath, GetEmailHash(email));
                File.Copy(file, store, true);
                NotifyResourceChanged(email, image);
            }
            catch
            {
                // ignore
            }
        }

        private void LoadDefaultAvatar(string key, string img)
        {
            var icon = AssetLoader.Open(new Uri($"avares://SourceGit/Resources/Images/{img}", UriKind.RelativeOrAbsolute));
            _cache.AddOrUpdate(key, new Bitmap(icon));
            _defaultAvatars.Add(key);
        }

        private string GetEmailHash(string email)
        {
            var lowered = email.ToLower(CultureInfo.CurrentCulture).Trim();
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(lowered));
            return Convert.ToHexStringLower(hash);
        }

        private void NotifyResourceChanged(string email, Bitmap image)
        {
            foreach (var avatar in _avatars)
                avatar.OnAvatarResourceChanged(email, image);
        }

        private class Cache
        {
            private readonly int _capacity;
            private readonly LinkedList<Item> _imgList;
            private readonly Dictionary<string, LinkedListNode<Item>> _key2img;

            private class Item
            {
                public string Key { get; }
                public Bitmap Image { get; set; }

                public Item(string key, Bitmap image)
                {
                    Key = key;
                    Image = image;
                }
            }

            public Cache(int capacity)
            {
                _capacity = capacity;
                _key2img = new Dictionary<string, LinkedListNode<Item>>();
                _imgList = new LinkedList<Item>();
            }

            public bool TryGet(string key, out Bitmap bitmap)
            {
                if (_key2img.TryGetValue(key, out var node))
                {
                    if (node.Value.Image == null)
                    {
                        _imgList.Remove(node);
                        _key2img.Remove(key);
                        bitmap = null;
                        return false;
                    }

                    _imgList.Remove(node);
                    _imgList.AddFirst(node);
                    bitmap = node.Value.Image;
                    return true;
                }

                bitmap = null;
                return false;
            }

            public void AddOrUpdate(string key, Bitmap bitmap)
            {
                if (bitmap == null)
                {
                    Remove(key);
                    return;
                }

                if (_key2img.TryGetValue(key, out var node))
                {
                    _imgList.Remove(node);
                    _imgList.AddFirst(node);
                    node.Value.Image = bitmap;
                    return;
                }

                if (_key2img.Count >= _capacity)
                {
                    var lastNode = _imgList.Last;
                    _imgList.RemoveLast();
                    _key2img.Remove(lastNode.Value.Key);
                }

                var newNode = new LinkedListNode<Item>(new Item(key, bitmap));
                _imgList.AddFirst(newNode);
                _key2img[key] = newNode;
            }

            public void Remove(string key)
            {
                if (_key2img.TryGetValue(key, out var node))
                {
                    _imgList.Remove(node);
                    _key2img.Remove(key);
                }
            }
        }
    }
}
