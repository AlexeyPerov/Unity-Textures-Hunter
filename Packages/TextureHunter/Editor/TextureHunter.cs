// #define HUNT_ADDRESSABLES

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
#if HUNT_ADDRESSABLES
using UnityEditor.AddressableAssets;
#endif

// ReSharper disable once CheckNamespace
namespace TextureHunter
{
    public class TextureHunterWindow : EditorWindow
    {
        private class Result
        {
            public List<AtlasData> Atlases { get; } = new List<AtlasData>();
            public List<TextureData> Textures { get; } = new List<TextureData>();
            public string OutputDescription { get; set; }
        }
        
        private class AnalysisSettings
        {
            public const int DefaultGCStep = 100000;
            
            public bool MipMapsAreErrors { get; set; } = true;
            public bool ReadableAreErrors { get; set; }
            public bool SizeHigher4KAreErrors { get; set; } = true;
            public bool NoOverridenCompressionAsErrors { get; set; } = true;
            public int GarbageCollectStep { get; set; } = DefaultGCStep;
            
            // limit number of assets in analysis to perform it faster for debug purposes
            public int DebugLimit;
            
            public List<TextureImporterFormat> RecommendedFormats { get; set; } = new List<TextureImporterFormat>
            {
                TextureImporterFormat.ASTC_5x5,
                TextureImporterFormat.ASTC_6x6,
                TextureImporterFormat.ASTC_8x8,
                TextureImporterFormat.ASTC_10x10,
                TextureImporterFormat.ASTC_12x12,
                TextureImporterFormat.ETC2_RGBA8Crunched
            };
        }

        private class SearchPatternsSettings
        {
            // ReSharper disable once StringLiteralTypo
            public readonly List<string> DefaultIgnorePatterns = new List<string>
            {
                @"/Editor/",
                @"/Editor Default Resources/",
                @"/Editor Resources/",
                @"ProjectSettings/",
                @"Packages/"
            };

            // ReSharper disable once InconsistentNaming
            public const string PATTERNS_PREFS_KEY = "TextureHunterIgnoreSearchPatterns";

            public List<string> IgnoredPatterns { get; set; }
        }
     
        private enum OutputFilterType
        {
            Textures,
            Atlases
        }
        
        private class OutputSettings
        {
            public const int PageSize = 50;

            public string PathFilter { get; set; }
            public OutputFilterType TypeFilter { get; set; }
            
            public TexturesOutputSettings TexturesSettings { get; } = new TexturesOutputSettings();
            public AtlasesOutputSettings AtlasesSettings { get; } = new AtlasesOutputSettings();
        }

        private class AtlasesOutputSettings : IPaginationSettings
        {
            public int? PageToShow { get; set; } = 0;
            
            /// <summary>
            /// Sorting types.
            /// By warning level: 0: A-Z, 1: Z-A
            /// By path: 2: A-Z, 3: Z-A
            /// By size: 4: A-Z, 5: Z-A
            /// </summary>
            public int SortType { get; set; }
            
            public bool WarningsOnly { get; set; }
        }
        
        private class TexturesOutputSettings : IPaginationSettings
        {
            public int? PageToShow { get; set; } = 0;
            
            /// <summary>
            /// Sorting types.
            /// By warning level: 0: A-Z, 1: Z-A
            /// By path: 2: A-Z, 3: Z-A
            /// By size: 4: A-Z, 5: Z-A
            /// </summary>
            public int SortType { get; set; }
            
            public bool WarningsOnly { get; set; }
        }
        
        private interface IPaginationSettings
        {
            int? PageToShow { get; set; }
        }

        private abstract class ItemDataBase
        {
            public int WarningLevel { get; private set; }
            
            public void TrySetWarningLevel(int level)
            {
                if (level <= WarningLevel) return;
                WarningLevel = level;
            }

            public List<string> CustomWarnings { get; private set; }

            public void AddCustomWarning(string warning)
            {
                CustomWarnings ??= new List<string>();
                CustomWarnings.Add(warning);
            }
        }
        
        private class AtlasData : ItemDataBase
        {
            public string Path { get; }
            public string Name => System.IO.Path.GetFileName(Path);
            public Type Type { get; }
            public string TypeName { get; }
            public string ReadableSize { get; }
            public List<PackableData> Packables { get; }
            public bool Foldout { get; set; }
            public Dictionary<string, AtlasPlatformImportSettings> ImportSettings { get; } =
                new Dictionary<string, AtlasPlatformImportSettings>();
            public int SpritesCount { get; private set; }
            public SpriteAtlas Atlas { get; }
            
            public AtlasData(
                SpriteAtlas atlas,
                string path,
                Type type,
                string typeName,
                string readableSize,
                Dictionary<string, List<TextureData>> packablesDictionary)
            {
                Atlas = atlas;
                Path = path;
                Type = type;
                TypeName = typeName;
                ReadableSize = readableSize;
                Packables = new List<PackableData>();

                foreach (var pair in packablesDictionary)
                {
                    Packables.Add(new PackableData(pair.Key, pair.Value));
                }
            }
            
            public void UpdateSpritesCount()
            {
                SpritesCount = Packables.Sum(packable => packable.Content.Count);
            }
        }
        
        private class AtlasPlatformImportSettings
        {
            public AtlasPlatformImportSettings(
                TextureImporterPlatformSettings settings,
                bool isDefault,
                TextureImporterFormat defaultFormat)
            {
                Settings = settings;

                FormatSet = Settings.format;
 
                CompressionQuality = Settings.compressionQuality;

                IsDefaultPlatform = isDefault;
                IsUsingDefaultSettings = !Settings.overridden;

                if (!isDefault && IsUsingDefaultSettings)
                {
                    FormatActual = defaultFormat;

                    if (FormatActual == TextureImporterFormat.Automatic)
                    {
                        Description = "Automatic";
                    }
                    else
                    {
                        Description = "Automatic -> " + FormatActual;
                    }
                }
                else
                {
                    FormatActual = FormatSet;
                    Description = FormatActual.ToString();
                }

                Description += $"[Q{CompressionQuality}]";
            }
            
            public bool IsDefaultPlatform { get; private set; }
            public TextureImporterPlatformSettings Settings { get; }
            private TextureImporterFormat FormatSet { get; }
            public TextureImporterFormat FormatActual { get; }
            private int CompressionQuality { get; }
            public string Description { get; }
            public bool IsUsingDefaultSettings { get; }
        }

        private class PackableData
        {
            public PackableData(string key, List<TextureData> content)
            {
                Key = key;
                Content = content;
            }

            public string Key { get; }
            public List<TextureData> Content { get; }
        }
        
        private class TextureData : ItemDataBase
        {
            private bool _importerLoaded;
            private bool _textureLoaded;
            
            private TextureImporter _importer;
            private Texture _texture;

            public TextureData(
                string path, 
                Type type,
                string typeName,
                long bytesSize, 
                string readableSize)
            {
                Path = path;
                Type = type;
                TypeName = typeName;
                BytesSize = bytesSize;
                ReadableSize = readableSize;

                InResources = Path.Contains("/Resources/");

                IsAddressable = CommonUtilities.IsAssetAddressable(Path);
            }

            public string Path { get; }
            public string Name => System.IO.Path.GetFileName(Path);
            public Type Type { get; }
            public string TypeName { get; }
            public long BytesSize { get; }
            public string ReadableSize { get; }
            public bool Foldout { get; set; }
            
            public bool InResources { get; }
            public bool IsAddressable { get; }

            public Dictionary<string, TexturePlatformImportSettings> ImportSettings { get; } =
                new Dictionary<string, TexturePlatformImportSettings>();

            public AtlasData Atlas { get; set; }
            
            public TextureImporter Importer
            {
                get
                {
                    if (_importerLoaded) return _importer;
                    _importerLoaded = true;
                    _importer = AssetImporter.GetAtPath(Path) as TextureImporter;
                    return _importer;
                }
            }

            private TextureInfo _info;
            
            public TextureInfo Info
            {
                get
                {
                    if (_info != null) 
                        return _info;
                    
                    var texture = Texture;

                    if (texture == null)
                        return null;

                    var width = texture.width;
                    var height = texture.height;
                        
                    var isPot = CommonUtilities.IsPowerOfTwo(texture.width) &&
                                CommonUtilities.IsPowerOfTwo(texture.height);

                    var isMultipleOfFour = texture.width % 4 == 0 && texture.height % 4 == 0;
                        
                    _info = new TextureInfo(width, height, isPot, isMultipleOfFour);

                    _texture = null;

                    return _info;
                }
            }

            private Texture Texture
            {
                get
                {
                    if (_textureLoaded) return _texture;
                    _textureLoaded = true;
                    _texture = EditorGUIUtility.Load(Path) as Texture;
                    return _texture;
                }
            }
        }

        private class TexturePlatformImportSettings
        {
            public TexturePlatformImportSettings(TextureImporter importer,
                string platform)
            {
                IsDefaultPlatform = platform == "Default";
                Settings = IsDefaultPlatform ? importer.GetDefaultPlatformTextureSettings() 
                    : importer.GetPlatformTextureSettings(platform);

                FormatSet = Settings.format;

                CompressionQuality = Settings.compressionQuality;

                IsUsingDefaultSettings = FormatSet == TextureImporterFormat.Automatic;

                if (IsUsingDefaultSettings)
                {
                    FormatActual = importer.GetAutomaticFormat(platform);

                    if (FormatActual == TextureImporterFormat.Automatic)
                    {
                        Description = "Automatic";
                    }
                    else
                    {
                        Description = "Automatic -> " + FormatActual;
                    }
                }
                else
                {
                    FormatActual = FormatSet;
                    Description = FormatActual.ToString();
                }

                Description += $"[Q{CompressionQuality}]";

                ActualFormatAsLoweredString = FormatActual.ToString().ToLowerInvariant();
            }
            
            public TextureImporterPlatformSettings Settings { get; }
            private TextureImporterFormat FormatSet { get; }
            public TextureImporterFormat FormatActual { get; }
            private int CompressionQuality { get; }
            public string Description { get; }
            public string ActualFormatAsLoweredString { get; }
            public bool IsUsingDefaultSettings { get; }
            public bool IsDefaultPlatform { get; }
        }

        private class TextureInfo
        {
            public TextureInfo(int width, int height, bool isPot, bool isMultipleOfFour)
            {
                Width = width;
                Height = height;
                IsPot = isPot;
                IsMultipleOfFour = isMultipleOfFour;
            }

            public int Width { get; }
            public int Height { get; }
            public bool IsPot { get; }
            public bool IsMultipleOfFour { get; }
        }
        
        [MenuItem("Tools/Texture Hunter")]
        public static void LaunchWindow()
        {
            GetWindow<TextureHunterWindow>();
        }
        
        private static void Clear()
        {
            EditorUtility.UnloadUnusedAssetsImmediate();
        }
        
        private void OnDestroy()
        {
            Clear();
        }
        
        private Result _result;
        private OutputSettings _outputSettings;
        private AnalysisSettings _analysisSettings;
        private SearchPatternsSettings _searchPatternsSettings;
        
        private bool _analysisSettingsFoldout;
        private bool _batchOperationsFoldout;
        private bool _searchPatternsSettingsFoldout;
        
        private bool _batchOperationsJustLog;
        
        private Vector2 _atlasesPagesScroll = Vector2.zero;
        private Vector2 _atlasesScroll = Vector2.zero;
        
        private Vector2 _texturesPagesScroll = Vector2.zero;
        private Vector2 _texturesScroll = Vector2.zero;
        
        // ReSharper disable once IdentifierTypo
        private const string WarningDuplicateInAddressables = "Possible duplicate in build: " +
                                                              "this texture is addressable and in atlas";
        private const string WarningDuplicateInResources = "Possible duplicate in build: " +
                                                           "this texture is in Resources and in atlas";
        private const string DuplicateInAtlas = "Duplicate in atlas: ";

        private const string AtlasContainsTextureThatExistsInAnotherAtlas
            = "Contains texture {0} that exists in another atlas";
        
        private const string DimensionsFallbackIssue = "Texture is neither POT nor multiple of 4: " +
                                                       "possible compression issue";

        private bool _analysisOngoing;

        private IEnumerator PopulateAssetsList()
        {
            _analysisOngoing = true;
            
            _result = new Result();
            _outputSettings ??= new OutputSettings();

            if (_analysisSettings.GarbageCollectStep < 0)
            {
                _analysisSettings.GarbageCollectStep = AnalysisSettings.DefaultGCStep;
            }

            Clear();
            Show();
            
            EditorUtility.ClearProgressBar();

            var assetPaths = AssetDatabase.GetAllAssetPaths();
            
            var filteredOutput = new StringBuilder();
            filteredOutput.AppendLine("Assets ignored by pattern:");
            
            var count = 0;

            for (var assetIndex = 0; assetIndex < assetPaths.Length; assetIndex++)
            {
                if (_analysisSettings.GarbageCollectStep != 0 && assetIndex % _analysisSettings.GarbageCollectStep == 0)
                {
                    GC.Collect();
                    yield return 0.05f;
                    GC.Collect();
                }
                
                var assetPath = assetPaths[assetIndex];
                EditorUtility.DisplayProgressBar("Textures Hunter", "Scanning for atlases",
                    (float)count / assetPaths.Length);

                var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);

                var validAssetType = type != null;

                if (!validAssetType)
                    continue;

                if (type == typeof(SpriteAtlas))
                {
                    var validForOutput = IsValidForOutput(assetPath,
                        _searchPatternsSettings.IgnoredPatterns);

                    if (!validForOutput)
                    {
                        filteredOutput.AppendLine(assetPath);
                        continue;
                    }

                    count++;

                    _result.Atlases.Add(CreateAtlasData(assetPath));

                    if (_analysisSettings.DebugLimit > 0 && _result.Atlases.Count > _analysisSettings.DebugLimit)
                        break;
                }
            }

            for (var assetIndex = 0; assetIndex < assetPaths.Length; assetIndex++)
            {
                if (_analysisSettings.GarbageCollectStep != 0 && assetIndex % _analysisSettings.GarbageCollectStep == 0)
                {
                    GC.Collect();
                    yield return 0.05f;
                    GC.Collect();
                }
                
                var assetPath = assetPaths[assetIndex];
                EditorUtility.DisplayProgressBar("Textures Hunter", "Scanning for textures",
                    (float)count / assetPaths.Length);

                var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);

                var validAssetType = type != null;

                if (!validAssetType)
                    continue;

                if (type != typeof(SpriteAtlas))
                {
                    count++;
                }

                if (type == typeof(Texture) || type == typeof(Texture2D))
                {
                    var textureData = CreateTextureData(assetPath);
                    var atlasFound = TryProcessAsAtlasTexture(textureData);

                    if (!atlasFound)
                    {
                        var validForOutput = IsValidForOutput(assetPath,
                            _searchPatternsSettings.IgnoredPatterns);

                        if (!validForOutput)
                        {
                            filteredOutput.AppendLine(assetPath);
                            continue;
                        }

                        ProcessAsNonAtlasTexture(textureData);

                        _result.Textures.Add(textureData);
                        
                        if (_analysisSettings.DebugLimit > 0 && _result.Textures.Count > _analysisSettings.DebugLimit)
                            break;
                    }
                }
            }
            
            GC.Collect();
            
            if (_analysisSettings.GarbageCollectStep != 0)
            {
                yield return 0.1f;
                GC.Collect();
            }

            PostProcessAtlases();

            _result.OutputDescription = $"Atlases: {_result.Atlases.Count}. Textures: {_result.Textures.Count}";

            SortAtlasesByWarnings(_result.Atlases, _outputSettings.AtlasesSettings);
            SortTexturesByWarnings(_result.Textures, _outputSettings.TexturesSettings);

            EditorUtility.ClearProgressBar();
            
            Debug.Log(filteredOutput.ToString());
            Debug.Log(_result.OutputDescription);
            filteredOutput.Clear();

            _analysisOngoing = false;
        }

        private bool TryProcessAsAtlasTexture(TextureData textureData)
        {
            var atlasFound = false;
            AtlasData atlasCandidate = null;
            PackableData packableCandidate = null;
            
            foreach (var atlas in _result.Atlases)
            {
                foreach (var packable in atlas.Packables)
                {
                    var isFolder = !Path.HasExtension(packable.Key);

                    bool isAddedDirectly;
                    bool isAddedViaFolder;

                    if (isFolder)
                    {
                        isAddedDirectly = false;
                        
                        // Unity may store path with separators from another OS
                        // so we need to check both cases and cannot just use Path.DirectorySeparatorChar
                        
                        // we have to ensure that there is a separator in the end of a packable folder
                        // to filter folders which start with the same substring (like 'Asses/Buildings/' and 'Assets/BuildingsIcons/')
                        
                        var endsAsOnNix = packable.Key.EndsWith("/");
                        var endsAsOnWindows = packable.Key.EndsWith("\\");
                        
                        if (endsAsOnNix || endsAsOnWindows)
                        {
                            isAddedViaFolder = textureData.Path.Contains(packable.Key);
                        }
                        else
                        {
                            isAddedViaFolder = textureData.Path.Contains(packable.Key + "/") ||
                                               textureData.Path.Contains(packable.Key + "\\");
                        }
                    }
                    else
                    {
                        isAddedViaFolder = false;
                        isAddedDirectly = textureData.Path == packable.Key;
                    }
                    
                    if (isAddedDirectly || isAddedViaFolder)
                    {
                        atlasFound = true;

                        if (atlasCandidate != null)
                        {
                            textureData.AddCustomWarning($"This texture's links to atlases ({atlas.Name}, {atlasCandidate.Name}) are ambiguous. " +
                                                         "While Unity probably handles it in a deterministic way we still mark is as a warning because it may be error-prone for users.");
                            textureData.TrySetWarningLevel(2);
                            
                            atlas.AddCustomWarning($"Atlas has ambiguous packables with atlas {atlasCandidate.Name} and its packable {packableCandidate.Key}");
                            atlas.TrySetWarningLevel(2);
                            
                            atlasCandidate.AddCustomWarning($"Atlas has ambiguous packables with atlas {atlas.Name} and its packable {packable.Key}");
                            atlasCandidate.TrySetWarningLevel(2);

                            if (packableCandidate.Key.Length > packable.Key.Length)
                            {
                                // Unity usually prefers more concrete packables - so do us.
                                // We assume that the larger the path the more concrete the packable. 
                                continue;
                            }
                        }
                        
                        atlasCandidate = atlas;
                        packableCandidate = packable;
                    }
                }
            }

            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            if (atlasCandidate != null && packableCandidate != null)
            {
                ApplyTextureToAtlas(textureData, atlasCandidate, packableCandidate);
            }

            return atlasFound;
        }

        private void ApplyTextureToAtlas(TextureData textureData, AtlasData atlas, PackableData packable)
        {
            if (textureData.IsAddressable)
            {
                textureData.AddCustomWarning(WarningDuplicateInAddressables);
                textureData.TrySetWarningLevel(1);
                atlas.TrySetWarningLevel(1);
            }
                            
            if (textureData.Atlas != null)
            {
                textureData.AddCustomWarning(DuplicateInAtlas + textureData.Atlas.Name);
                textureData.TrySetWarningLevel(3);

                textureData.Atlas.TrySetWarningLevel(2);
                textureData.Atlas.AddCustomWarning(
                    string.Format(AtlasContainsTextureThatExistsInAnotherAtlas, 
                        textureData.Name));
            }
                        
            textureData.Atlas = atlas;
                        
            if (textureData.InResources)
            {
                textureData.AddCustomWarning(WarningDuplicateInResources);
                textureData.TrySetWarningLevel(2);
            }
                        
            packable.Content.Add(textureData);
        }

        private void ProcessAsNonAtlasTexture(TextureData textureData)
        {
            var info = textureData.Info;

            if (!info.IsPot && !info.IsMultipleOfFour)
            {
                textureData.TrySetWarningLevel(1);
                textureData.AddCustomWarning(DimensionsFallbackIssue);
            }

            if (_analysisSettings.SizeHigher4KAreErrors && (info.Width > 4096 || info.Height > 4096))
            {
                textureData.TrySetWarningLevel(2);
                textureData.AddCustomWarning("Size over 4096");
            }

            var importer = textureData.Importer;

            if (importer == null)
            {
                textureData.TrySetWarningLevel(2);
                textureData.AddCustomWarning("Unable to load an importer");
                return;
            }

            if (_analysisSettings.MipMapsAreErrors && importer.mipmapEnabled)
            {
                textureData.TrySetWarningLevel(2);
                textureData.AddCustomWarning("Mipmap is enabled. Is it intended?");
            }

            if (_analysisSettings.ReadableAreErrors && importer.isReadable)
            {
                textureData.TrySetWarningLevel(2);
                textureData.AddCustomWarning("Texture is readable. Is it intended?");
            }

            var iOSSettings = new TexturePlatformImportSettings(importer, "iOS");
            var androidSettings = new TexturePlatformImportSettings(importer, "Android");

            if (_analysisSettings.NoOverridenCompressionAsErrors)
            {
                if (iOSSettings.IsUsingDefaultSettings || androidSettings.IsUsingDefaultSettings)
                {
                    textureData.TrySetWarningLevel(2);
                    textureData.AddCustomWarning("Texture uses Automatic compression. Is it intended?");
                }
            }
            
            textureData.ImportSettings["iOS"] = iOSSettings;
            textureData.ImportSettings["Android"] = androidSettings;

            textureData.ImportSettings["Default"] = new TexturePlatformImportSettings(importer, "Default");

            foreach (var settings in textureData.ImportSettings)
            {
                if (settings.Value.ActualFormatAsLoweredString.Contains("crunch") && !info.IsMultipleOfFour)
                {
                    textureData.TrySetWarningLevel(2);
                    textureData.AddCustomWarning(
                        $"{settings.Key}: only multiple of 4 textures can use crunch compression");
                }

                if (settings.Value.ActualFormatAsLoweredString.Contains("pvrtc") && !info.IsPot)
                {
                    textureData.TrySetWarningLevel(2);
                    textureData.AddCustomWarning(
                        $"{settings.Key}: only POT textures can use PVRTC format");
                }

                if (!settings.Value.IsDefaultPlatform && !_analysisSettings.RecommendedFormats.Contains(settings.Value.FormatActual))
                {
                    textureData.TrySetWarningLevel(2);
                    textureData.AddCustomWarning(
                        $"{settings.Key}: does not use recommended compression");
                }
            }
        }

        private void PostProcessAtlases()
        {
            foreach (var atlas in _result.Atlases)
            {
                atlas.UpdateSpritesCount();

                if (atlas.Packables.Count == 0)
                {
                    atlas.TrySetWarningLevel(2);
                    atlas.AddCustomWarning("Packables list is empty");
                }
                else if (atlas.SpritesCount == 0)
                {
                    atlas.TrySetWarningLevel(1);
                    atlas.AddCustomWarning("Unable to detect sprites. Might be an issue with packables or this tool could not find sprites within subfolders." +
                                           "We mark it as a warning because we suggest that this atlas settings might be confusing for users.");
                }
            }
        }

        private bool IsValidForOutput(string path, List<string> ignoreInOutputPatterns)
        {
            return ignoreInOutputPatterns.All(pattern 
                => string.IsNullOrEmpty(pattern) || !Regex.Match(path, pattern).Success);
        }

        private AtlasData CreateAtlasData(string path)
        {
            var fileInfo = new FileInfo(path);
            var bytesSize = fileInfo.Length;
                
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            var typeName = CommonUtilities.GetReadableTypeName(type);

            var atlas = EditorGUIUtility.Load(path) as SpriteAtlas;
            
            var packables = atlas.GetPackables();

            var defaultsAssets = packables.OfType<DefaultAsset>();
            var folders = defaultsAssets.Select(AssetDatabase.GetAssetPath).ToList();

            var packablesDictionary = folders.ToDictionary(folder => folder, _ => new List<TextureData>());

            var directTextures = packables.OfType<Texture2D>();

            foreach (var directTexture in directTextures)
            {
                var textureName = AssetDatabase.GetAssetPath(directTexture);
                if (!packablesDictionary.ContainsKey(textureName))
                {
                    packablesDictionary.Add(textureName, new List<TextureData>());
                }
                else
                {
                    Debug.LogWarning($"Texture name [{textureName}]" +
                                     $" is presented in the atlas [{path}] twice");
                }
            }

            var atlasData = new AtlasData(atlas, path, type, typeName, 
                CommonUtilities.GetReadableSize(bytesSize), packablesDictionary);

            ProcessSpriteAtlasTexture(atlasData, atlas);
            
            return atlasData;
        }

        private void ProcessSpriteAtlasTexture(AtlasData atlasData, SpriteAtlas atlas)
        {
            var iOSAutomatic = false;
            var androidAutomatic = false;
            
            var textureSettings = atlas.GetTextureSettings();
            

            var defaultPlatformSettings = atlas.GetPlatformSettings("DefaultTexturePlatform");

            if (defaultPlatformSettings == null)
            {
                atlasData.TrySetWarningLevel(2);
                atlasData.AddCustomWarning("Unable to retrieve default importer settings");
                return;
            }
            
            var defaultSettings = new AtlasPlatformImportSettings(defaultPlatformSettings, true, defaultPlatformSettings.format); 
            atlasData.ImportSettings["Default"] = defaultSettings;

            var androidPlatformSettings = atlas.GetPlatformSettings("Android");

            if (androidPlatformSettings != null)
            {
                var androidSettings =
                    new AtlasPlatformImportSettings(androidPlatformSettings, false, defaultPlatformSettings.format);
                androidAutomatic = androidSettings.IsUsingDefaultSettings;
                atlasData.ImportSettings["Android"] = androidSettings;
                
                if (!_analysisSettings.RecommendedFormats.Contains(androidSettings.FormatActual))
                {
                    atlasData.TrySetWarningLevel(2);
                    atlasData.AddCustomWarning(
                        $"Does not use recommended compression");
                }
            }

            var iOSPlatformSettings = atlas.GetPlatformSettings("iPhone");

            if (iOSPlatformSettings != null)
            {
                var iOSSettings =
                    new AtlasPlatformImportSettings(iOSPlatformSettings, false, defaultPlatformSettings.format);
                iOSAutomatic = iOSSettings.IsUsingDefaultSettings;
                atlasData.ImportSettings["iOS"] = iOSSettings;
                
                if (!_analysisSettings.RecommendedFormats.Contains(iOSSettings.FormatActual))
                {
                    atlasData.TrySetWarningLevel(2);
                    atlasData.AddCustomWarning(
                        $"Does not use recommended compression");
                }
            }

            if (_analysisSettings.MipMapsAreErrors && textureSettings.generateMipMaps)
            {
                atlasData.TrySetWarningLevel(2);
                atlasData.AddCustomWarning("Mipmap is enabled. Is it intended?");
            }
            
            if (_analysisSettings.NoOverridenCompressionAsErrors)
            {
                if (iOSAutomatic || androidAutomatic)
                {
                    atlasData.TrySetWarningLevel(2);
                    atlasData.AddCustomWarning("Atlas uses Automatic compression. Is it intended?");
                }
            }
        }
        
        private TextureData CreateTextureData(string path)
        {
            var fileInfo = new FileInfo(path);
            var bytesSize = fileInfo.Length;
                
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            var typeName = CommonUtilities.GetReadableTypeName(type);
                
            return new TextureData(path, type, typeName, bytesSize, CommonUtilities.GetReadableSize(bytesSize));
        }
        
        private void SetAtlasesQuality(string platform, bool performChanges = true, int crunchedQuality = 30, int astcQuality = 50)
        {
            if (_result == null)
                return;

            var counter = 0;
            
            foreach (var atlas in _result.Atlases)
            {
                foreach (var setting in atlas.ImportSettings)
                {
                    if (!setting.Value.IsDefaultPlatform && !setting.Value.IsUsingDefaultSettings && setting.Key == platform)
                    {
                        if (setting.Value.FormatActual == TextureImporterFormat.ETC2_RGBA8Crunched)
                        {
                            if (setting.Value.Settings.compressionQuality != crunchedQuality)
                            {
                                Debug.LogWarning($"Changing: {atlas.Name} / {setting.Key} quality to {crunchedQuality}");

                                if (performChanges)
                                {
                                    setting.Value.Settings.compressionQuality = crunchedQuality;
                                    atlas.Atlas.SetPlatformSettings(setting.Value.Settings);
                                }

                                counter++;
                            }
                        }
                        else if (CommonUtilities.IsAnyAstc(setting.Value.FormatActual))
                        {
                            var isDirty = false;
                            
                            var redundantLevel =
                                setting.Value.FormatActual == TextureImporterFormat.ASTC_5x5 || setting.Value.FormatActual == TextureImporterFormat.ASTC_4x4;

                            if (redundantLevel)
                            {
                                Debug.LogWarning(
                                    $"Changing: {atlas.Name} / {setting.Key} format to {TextureImporterFormat.ASTC_6x6}");
                                
                                if (performChanges)
                                    setting.Value.Settings.format = TextureImporterFormat.ASTC_6x6;
                                isDirty = true;
                            }

                            if (setting.Value.Settings.compressionQuality != astcQuality)
                            {
                                Debug.LogWarning(
                                    $"Changing: {atlas.Name} / {setting.Key} format to quality {astcQuality}");
                                
                                if (performChanges)
                                    setting.Value.Settings.compressionQuality = astcQuality;
                                isDirty = true;
                            }

                            if (isDirty)
                            {
                                if (performChanges)
                                    atlas.Atlas.SetPlatformSettings(setting.Value.Settings);
                                counter++;
                            }
                        }
                    }
                }
            }

            Debug.LogWarning($"Changed {counter} atlases");
            
            AssetDatabase.SaveAssets();
        }

        private IEnumerator SetTexturesQuality(string platform, bool performChanges = true, int crunchedQuality = 30, int astcQuality = 50)
        {
            if (_result == null)
                yield break;

            _analysisOngoing = true;
            
            var changeCounter = 0;

            foreach (var texture in _result.Textures)
            {
                var prevCounter = changeCounter;
                
                SetTextureQuality(platform, ref changeCounter, texture, performChanges, crunchedQuality, astcQuality);
                
                if (prevCounter != changeCounter && changeCounter % 100 == 0)
                {
                    GC.Collect();
                    Debug.Log($"GC call. Changed {changeCounter} textures");
                    yield return 0.05f;
                    GC.Collect();
                }
            }
            
            Debug.LogWarning($"Changed {changeCounter} textures");
            
            _analysisOngoing = false;
            
            AssetDatabase.SaveAssets();
        }

        private void SetTextureQuality(string platform, ref int counter, TextureData texture, bool performChanges = true, 
            int crunchedQuality = 30, int astcQuality = 50)
        {
            foreach (var setting in texture.ImportSettings)
            {
                if (!setting.Value.IsDefaultPlatform && !setting.Value.IsUsingDefaultSettings && setting.Key == platform)
                {
                    if (setting.Value.FormatActual == TextureImporterFormat.ETC2_RGBA8Crunched)
                    {
                        if (setting.Value.Settings.compressionQuality != crunchedQuality)
                        {
                            var importer = texture.Importer;

                            if (importer != null)
                            {
                                Debug.LogWarning(
                                    $"Changing: {texture.Name} / {setting.Key} quality to {crunchedQuality}");

                                if (performChanges)
                                {
                                    setting.Value.Settings.compressionQuality = crunchedQuality;

                                    importer.SetPlatformTextureSettings(setting.Value.Settings);
                                    importer.SaveAndReimport();
                                }

                                counter++;
                            }
                        }
                    }
                    else if (CommonUtilities.IsAnyAstc(setting.Value.FormatActual))
                    {
                        var importer = texture.Importer;

                        if (importer != null)
                        {
                            var isDirty = false;

                            var redundantLevel =
                                setting.Value.FormatActual == TextureImporterFormat.ASTC_5x5 || setting.Value.FormatActual == TextureImporterFormat.ASTC_4x4;

                            if (redundantLevel)
                            {
                                Debug.LogWarning(
                                    $"Changing: {texture.Name} / {setting.Key} format to {TextureImporterFormat.ASTC_6x6}");
                                if (performChanges)
                                    setting.Value.Settings.format = TextureImporterFormat.ASTC_6x6;
                                isDirty = true;
                            }

                            if (setting.Value.Settings.compressionQuality != astcQuality)
                            {
                                Debug.LogWarning(
                                    $"Changing: {texture.Name} / {setting.Key} format to quality {astcQuality}");
                                if (performChanges)
                                    setting.Value.Settings.compressionQuality = astcQuality;
                                isDirty = true;
                            }

                            if (isDirty)
                            {
                                if (performChanges)
                                {
                                    importer.SetPlatformTextureSettings(setting.Value.Settings);
                                    importer.SaveAndReimport();
                                }

                                counter++;
                            }
                        }
                    }
                }
            }
        }
        
        private IEnumerator FixTexturesAutomaticCompression(string platform, TextureImporterFormat format, 
            int quality, bool performChanges = true)
        {
            if (_result == null)
                yield break;

            _analysisOngoing = true;
            
            var changeCounter = 0;

            foreach (var texture in _result.Textures)
            {
                if (texture.WarningLevel < 2)
                    continue;

                if (!texture.ImportSettings.ContainsKey(platform))
                    continue;

                var settings = texture.ImportSettings[platform];
                
                if (!settings.IsUsingDefaultSettings)
                    continue;

                settings.Settings.format = format;
                settings.Settings.compressionQuality = quality;

                if (performChanges)
                {
                    texture.Importer.SetPlatformTextureSettings(settings.Settings);
                    texture.Importer.SaveAndReimport();
                }
                
                changeCounter++;
                
                if (changeCounter % 100 == 0)
                {
                    GC.Collect();
                    Debug.Log($"GC call. Changed {changeCounter} textures");
                    yield return 0.05f;
                    GC.Collect();
                }
            }
            
            Debug.LogWarning($"Changed {changeCounter} textures");
            
            _analysisOngoing = false;
            
            AssetDatabase.SaveAssets();
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            
            GUILayout.FlexibleSpace();
            
            var prevColor = GUI.color;
            GUI.color = Color.green;

            if (!_analysisOngoing)
            {
                var postfix = _result != null ? " (Overrides last results)" : string.Empty;
                if (GUILayout.Button($"Run Analysis{postfix}", GUILayout.Width(300f)))
                {
                    PocketEditorCoroutine.Start(PopulateAssetsList(), this);
                }
            }
            else
            {
                GUILayout.Label("Analysis ongoing...");
            }

            GUI.color = prevColor;
            
            GUILayout.FlexibleSpace();
            
            EditorGUILayout.EndHorizontal();

            OnSearchPatternsSettingsGUI();
            OnAnalysisSettingsGUI();
            
            GUIUtilities.HorizontalLine();

            if (_result == null || _analysisOngoing)
            {
                return;
            }
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(_result.OutputDescription);

            EditorGUILayout.EndHorizontal();

            GUIUtilities.HorizontalLine();

            _batchOperationsFoldout = EditorGUILayout.Foldout(_batchOperationsFoldout, "Batch Operations");

            if (_batchOperationsFoldout)
            {
                _batchOperationsJustLog = EditorGUILayout.Toggle("Just log", _batchOperationsJustLog);
                
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("[Android] Atlases: Set ETC2 Crunched quality to 30; min ASTC to 6x6 and quality to 50"))
                {
                    SetAtlasesQuality("Android", !_batchOperationsJustLog);
                }

                if (GUILayout.Button("[Android] Textures: Set ETC2 Crunched quality to 30; min ASTC to 6x6 and quality to 50"))
                {
                    PocketEditorCoroutine.Start(SetTexturesQuality("Android", !_batchOperationsJustLog), this);
                }
                
                EditorGUILayout.EndHorizontal();
                
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("[iOS] Atlases: Set ETC2 Crunched quality to 30; min ASTC to 6x6 and quality to 50"))
                {
                    SetAtlasesQuality("iOS", !_batchOperationsJustLog);
                }

                if (GUILayout.Button("[iOS] Textures: Set ETC2 Crunched quality to 30; min ASTC to 6x6 and quality to 50"))
                {
                    PocketEditorCoroutine.Start(SetTexturesQuality("iOS", !_batchOperationsJustLog), this);
                }
                
                EditorGUILayout.EndHorizontal();
                
                EditorGUILayout.BeginHorizontal();
                
                if (GUILayout.Button("[Android] Textures: Override all non yet overriden textures to ASTC 8x8"))
                {
                    PocketEditorCoroutine.Start(FixTexturesAutomaticCompression("Android", TextureImporterFormat.ASTC_8x8, 50, !_batchOperationsJustLog), this);
                }
                
                if (GUILayout.Button("[iOS] Textures: Override all non yet overriden textures to ASTC 8x8"))
                {
                    PocketEditorCoroutine.Start(FixTexturesAutomaticCompression("iOS", TextureImporterFormat.ASTC_8x8, 50, !_batchOperationsJustLog), this);
                }
                
                EditorGUILayout.EndHorizontal();
            }

            GUIUtilities.HorizontalLine();

            EditorGUILayout.BeginHorizontal();

            prevColor = GUI.color;
            
            var prevAlignment = GUI.skin.button.alignment;
            GUI.skin.button.alignment = TextAnchor.MiddleLeft;
            
            GUI.color = _outputSettings.TypeFilter == OutputFilterType.Textures ? Color.yellow : Color.white;
            
            if (GUILayout.Button($"[{_result.Textures.Count}] Textures (non-atlas)", GUILayout.Width(200f)))
            {
                _outputSettings.TypeFilter = OutputFilterType.Textures;
            }
            
            GUI.color = _outputSettings.TypeFilter == OutputFilterType.Atlases ? Color.yellow : Color.white;
            
            if (GUILayout.Button($"[{_result.Atlases.Count}] Atlases", GUILayout.Width(200f)))
            {
                _outputSettings.TypeFilter = OutputFilterType.Atlases;
            }
            
            GUI.skin.button.alignment = prevAlignment;
            GUI.color = prevColor;

            EditorGUILayout.EndHorizontal();

            GUIUtilities.HorizontalLine();
            
            EditorGUILayout.BeginHorizontal();

            var textFieldStyle = EditorStyles.textField;
            var prevTextFieldAlignment = textFieldStyle.alignment;
            textFieldStyle.alignment = TextAnchor.MiddleCenter;
            
            _outputSettings.PathFilter = EditorGUILayout.TextField("Path Contains:", 
                _outputSettings.PathFilter, GUILayout.Width(400f));

            textFieldStyle.alignment = prevTextFieldAlignment;

            EditorGUILayout.EndHorizontal();
            
            

            GUIUtilities.HorizontalLine();

            switch (_outputSettings.TypeFilter)
            {
                case OutputFilterType.Atlases:
                    OnDrawAtlases(_result.Atlases, _outputSettings.PathFilter, _outputSettings.AtlasesSettings);
                    break;
                case OutputFilterType.Textures:
                    OnDrawTextures(_result.Textures, _outputSettings.PathFilter, _outputSettings.TexturesSettings);
                    break;
            }
        }

        private void OnDrawAtlases(List<AtlasData> atlases, string pathFilter, AtlasesOutputSettings settings)
        {
            if (_result.Atlases.Count == 0)
            {
                EditorGUILayout.LabelField("No atlases found");
                return;
            }

            EditorGUILayout.BeginHorizontal();
            
            var prevColor = GUI.color;

            var sortType = settings.SortType;
            
            GUI.color = sortType == 0 || sortType == 1 ? Color.yellow : Color.white;
            var orderType = sortType == 1 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by warnings " + orderType, GUILayout.Width(150f)))
            {
                SortAtlasesByWarnings(atlases, settings);
            }
        
            GUI.color = sortType == 2 || sortType == 3 ? Color.yellow : Color.white;
            orderType = sortType == 3 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by path " + orderType, GUILayout.Width(150f)))
            {
                SortAtlasesByPath(atlases, settings);
            }
            
            GUI.color = sortType == 4 || sortType == 5 ? Color.yellow : Color.white;
            orderType = sortType == 5 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by sprites count " + orderType, GUILayout.Width(200f)))
            {
                SortAtlasesBySpritesCount(atlases, settings);
            }
            
            GUI.color = settings.WarningsOnly ? Color.yellow : Color.white;
            if (GUILayout.Button("Warnings Level 2+ Only", GUILayout.Width(250f)))
            {
                settings.WarningsOnly = !settings.WarningsOnly;
            }
            
            GUI.color = prevColor;
            
            EditorGUILayout.EndHorizontal();
            
            var filteredAssets = atlases;

            if (settings.WarningsOnly)
            {
                filteredAssets = filteredAssets.Where(x => x.WarningLevel > 1).ToList();
            }
            
            if (!string.IsNullOrEmpty(pathFilter))
            {
                filteredAssets = filteredAssets.Where(x => x.Path.Contains(pathFilter)).ToList();
            }
            
            DrawPagesWidget(filteredAssets.Count, settings, ref _atlasesPagesScroll);
            
            GUIUtilities.HorizontalLine();
            
            _atlasesScroll = GUILayout.BeginScrollView(_atlasesScroll);

            EditorGUILayout.BeginVertical();

            for (var i = 0; i < filteredAssets.Count; i++)
            {
                if (settings.PageToShow.HasValue)
                {
                    var page = settings.PageToShow.Value;
                    if (i < page * OutputSettings.PageSize || i >= (page + 1) * OutputSettings.PageSize)
                    {
                        continue;
                    }
                }
                
                var asset = filteredAssets[i];
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(asset.Foldout ? ">Minimize" : ">Expand", GUILayout.Width(70)))
                {
                    asset.Foldout = !asset.Foldout;
                }
                                
                prevColor = GUI.color;
                
                if (asset.WarningLevel > 2)
                    GUI.color = Color.red;
                else if (asset.WarningLevel == 2)
                    GUI.color = Color.yellow;
                else if (asset.WarningLevel == 1)
                    GUI.color = new Color(0.44f, 0.79f, 1f);
                
                EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(40f));
                
                EditorGUILayout.LabelField(asset.TypeName, GUILayout.Width(150f));    
                
                EditorGUILayout.LabelField($"Warning: {asset.WarningLevel}", GUILayout.Width(70f));

                GUI.color = prevColor;
                
                var guiContent = EditorGUIUtility.ObjectContent(null, asset.Type);
                guiContent.text = Path.GetFileName(asset.Path);

                var alignment = GUI.skin.button.alignment;
                GUI.skin.button.alignment = TextAnchor.MiddleLeft;

                if (GUILayout.Button(guiContent, GUILayout.Width(300f), GUILayout.Height(18f)))
                {
                    Selection.objects = new[] { AssetDatabase.LoadMainAssetAtPath(asset.Path) };
                }

                GUI.skin.button.alignment = alignment;

                EditorGUILayout.LabelField("Sprites: " + asset.SpritesCount, GUILayout.Width(100f));
                
                foreach (var importSettings in asset.ImportSettings)
                {
                    EditorGUILayout.LabelField(importSettings.Key + " : " + importSettings.Value.Description, GUILayout.Width(235));
                }
                
                GUI.color = prevColor;
                
                EditorGUILayout.EndHorizontal();

                if (asset.Foldout)
                {
                    GUILayout.Space(3);
                    EditorGUILayout.LabelField($"Atlas Path: {asset.Path}. Self file size: {asset.ReadableSize}");

                    var textureIndex = 0;
                    
                    foreach (var packable in asset.Packables)
                    {
                        var isFolder = !Path.HasExtension(packable.Key);
                        EditorGUILayout.LabelField($"Packable {(isFolder ? "(folder)" : string.Empty)}: {packable.Key}");
                        
                        foreach (var textureData in packable.Content)
                        {
                            DrawTexture(textureIndex, textureData);
                            textureIndex++;
                        }
                    }
                    
                    GUIUtilities.HorizontalLine();
                    
                    if (asset.CustomWarnings != null)
                    {
                        EditorGUILayout.LabelField($"Warnings [{asset.CustomWarnings.Count}]:");
                        foreach (var customWarning in asset.CustomWarnings)
                        {
                            EditorGUILayout.LabelField(customWarning);
                        }
                        
                        GUIUtilities.HorizontalLine();
                    }
                }
            }

            GUILayout.FlexibleSpace();
            
            EditorGUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        private void OnDrawTextures(List<TextureData> textures, string pathFilter, TexturesOutputSettings settings)
        {
            if (_result.Textures.Count == 0)
            {
                EditorGUILayout.LabelField("No textures found");
                return;
            }

            EditorGUILayout.BeginHorizontal();
            
            var prevColor = GUI.color;

            var sortType = settings.SortType;
            
            GUI.color = sortType == 0 || sortType == 1 ? Color.yellow : Color.white;
            var orderType = sortType == 1 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by warnings " + orderType, GUILayout.Width(150f)))
            {
                SortTexturesByWarnings(textures, settings);
            }
        
            GUI.color = sortType == 2 || sortType == 3 ? Color.yellow : Color.white;
            orderType = sortType == 3 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by path " + orderType, GUILayout.Width(150f)))
            {
                SortTexturesByPath(textures, settings);
            }
            
            GUI.color = sortType == 4 || sortType == 5 ? Color.yellow : Color.white;
            orderType = sortType == 5 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by size " + orderType, GUILayout.Width(150f)))
            {
                SortTexturesBySize(textures, settings);
            }
            
            GUI.color = settings.WarningsOnly ? Color.yellow : Color.white;
            if (GUILayout.Button("Warnings Level 2+ Only", GUILayout.Width(250f)))
            {
                settings.WarningsOnly = !settings.WarningsOnly;
            }
            
            GUI.color = prevColor;
            
            EditorGUILayout.EndHorizontal();
            
            var filteredAssets = textures;
            
            if (settings.WarningsOnly)
            {
                filteredAssets = filteredAssets.Where(x => x.WarningLevel > 1).ToList();
            }
            
            if (!string.IsNullOrEmpty(pathFilter))
            {
                filteredAssets = filteredAssets.Where(x => x.Path.Contains(pathFilter)).ToList();
            }

            DrawPagesWidget(filteredAssets.Count, settings, ref _texturesPagesScroll);
            
            GUIUtilities.HorizontalLine();
            
            _texturesScroll = GUILayout.BeginScrollView(_texturesScroll);

            EditorGUILayout.BeginVertical();

            for (var i = 0; i < filteredAssets.Count; i++)
            {
                if (settings.PageToShow.HasValue)
                {
                    var page = settings.PageToShow.Value;
                    if (i < page * OutputSettings.PageSize || i >= (page + 1) * OutputSettings.PageSize)
                    {
                        continue;
                    }
                }
                
                var asset = filteredAssets[i];
                DrawTexture(i, asset);
            }

            GUILayout.FlexibleSpace();
            
            EditorGUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        private void DrawTexture(int i, TextureData asset)
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(asset.Foldout ? "Minimize" : "Expand", GUILayout.Width(70)))
            {
                asset.Foldout = !asset.Foldout;
            }

            var prevColor = GUI.color;

            if (asset.WarningLevel > 2)
                GUI.color = Color.red;
            else if (asset.WarningLevel == 2)
                GUI.color = Color.yellow;
            else if (asset.WarningLevel == 1)
                GUI.color = new Color(0.44f, 0.79f, 1f);

            EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(40f));

            EditorGUILayout.LabelField(asset.TypeName, GUILayout.Width(70f));

            EditorGUILayout.LabelField($"Warning: {asset.WarningLevel}", GUILayout.Width(70f));

            var guiContent = EditorGUIUtility.ObjectContent(null, asset.Type);
            guiContent.text = Path.GetFileName(asset.Path);

            var alignment = GUI.skin.button.alignment;
            GUI.skin.button.alignment = TextAnchor.MiddleLeft;

            if (GUILayout.Button(guiContent, GUILayout.Width(300f), GUILayout.Height(18f)))
            {
                Selection.objects = new[] { AssetDatabase.LoadMainAssetAtPath(asset.Path) };
            }

            GUI.skin.button.alignment = alignment;

            EditorGUILayout.LabelField(asset.ReadableSize, GUILayout.Width(70f));

            GUI.color = prevColor;

            if (asset.Info != null)
            {
                EditorGUILayout.LabelField($"{asset.Info.Width}x{asset.Info.Height}", GUILayout.Width(80));

                var isPot = asset.Info.IsPot;

                prevColor = GUI.color;
                GUI.color = isPot ? Color.green : Color.gray;

                EditorGUILayout.LabelField(isPot ? "POT" : "Non-POT", GUILayout.Width(60));

                var isMultipleOfFour = asset.Info.IsMultipleOfFour;

                GUI.color = isMultipleOfFour ? Color.green : Color.gray;
                EditorGUILayout.LabelField(isMultipleOfFour ? "Multiple of 4" : "Non Multiple of 4",
                    GUILayout.Width(120));

                GUI.color = prevColor;
            }
            else
            {
                prevColor = GUI.color;
                GUI.color = Color.red;
                EditorGUILayout.LabelField("Texture is null", GUILayout.Width(140));
                GUI.color = prevColor;
            }

            foreach (var settings in asset.ImportSettings)
            {
                EditorGUILayout.LabelField(settings.Key + " : " + settings.Value.Description, GUILayout.Width(235));
            }

            GUI.color = prevColor;

            EditorGUILayout.EndHorizontal();

            if (asset.Foldout)
            {
                GUILayout.Space(3);
                EditorGUILayout.LabelField($"Path: {asset.Path}");
                GUIUtilities.HorizontalLine();

                if (asset.CustomWarnings != null)
                {
                    EditorGUILayout.LabelField($"Warnings [{asset.CustomWarnings.Count}]:");
                    foreach (var customWarning in asset.CustomWarnings)
                    {
                        EditorGUILayout.LabelField(customWarning);
                    }
                    
                    GUIUtilities.HorizontalLine();
                }
            }
        }

        private void DrawPagesWidget(int assetsCount, IPaginationSettings settings, ref Vector2 scroll)
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.BeginHorizontal();
            
            var prevColor = GUI.color;
            GUI.color = !settings.PageToShow.HasValue ? Color.yellow : Color.white;

            if (GUILayout.Button("All", GUILayout.Width(30f)))
            {
                settings.PageToShow = null;
            }

            GUI.color = prevColor;
            
            var totalCount = assetsCount;
            var pagesCount = totalCount / OutputSettings.PageSize + (totalCount % OutputSettings.PageSize > 0 ? 1 : 0);

            for (var i = 0; i < pagesCount; i++)
            {
                prevColor = GUI.color;
                GUI.color = settings.PageToShow == i ? Color.yellow : Color.white;

                if (GUILayout.Button((i + 1).ToString(), GUILayout.Width(30f)))
                {
                    settings.PageToShow = i;
                }

                GUI.color = prevColor;
            }

            if (settings.PageToShow.HasValue && settings.PageToShow > pagesCount - 1)
            {
                settings.PageToShow = pagesCount - 1;
            }

            if (settings.PageToShow.HasValue && pagesCount == 0)
            {
                settings.PageToShow = null;
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
        }

        private void OnAnalysisSettingsGUI()
        {
            EnsureAnalysisSettingsLoaded();
            
            _analysisSettingsFoldout = EditorGUILayout.Foldout(_analysisSettingsFoldout,
                "Analysis Settings.");

            if (!_analysisSettingsFoldout) 
                return;
            
            GUILayout.BeginHorizontal();

            if (GUILayout.Button($"No platform overriden compression as error: {_analysisSettings.NoOverridenCompressionAsErrors}"))
            {
                _analysisSettings.NoOverridenCompressionAsErrors = !_analysisSettings.NoOverridenCompressionAsErrors;
            }
            
            if (GUILayout.Button($"MipmapEnabled as error: {_analysisSettings.MipMapsAreErrors}"))
            {
                _analysisSettings.MipMapsAreErrors = !_analysisSettings.MipMapsAreErrors;
            }
            
            if (GUILayout.Button($"(Non Atlas) IsReadable as error: {_analysisSettings.ReadableAreErrors}"))
            {
                _analysisSettings.ReadableAreErrors = !_analysisSettings.ReadableAreErrors;
            }
            
            if (GUILayout.Button($"(Non Atlas) Height/Width > 4k as error: {_analysisSettings.SizeHigher4KAreErrors}"))
            {
                _analysisSettings.SizeHigher4KAreErrors = !_analysisSettings.SizeHigher4KAreErrors;
            }
            
            GUILayout.EndHorizontal();
            
            GUILayout.Label(
                "*If you face OOM during analysis then try to lower GC parameter below");
            _analysisSettings.GarbageCollectStep = EditorGUILayout.IntField("GC once in (iterations):", _analysisSettings.GarbageCollectStep);
            
            GUILayout.Label(
                "*Below is a debug option to limit number of assets in the analysis");
            _analysisSettings.DebugLimit = EditorGUILayout.IntField("Assets Debug Limit:", _analysisSettings.DebugLimit);
            
            GUILayout.Label("Recommended Formats");

            var count = _analysisSettings.RecommendedFormats.Count;
            
            GUILayout.BeginHorizontal();

            if (count > 0)
            {
                if (GUILayout.Button("Remove"))
                {
                    count--;
                }
            }

            if (GUILayout.Button("Add"))
            {
                count++;
            }
            
            GUILayout.FlexibleSpace();

            GUILayout.EndHorizontal();
            
            if (count != _analysisSettings.RecommendedFormats.Count)
            {
                var newList = new List<TextureImporterFormat>(count);

                for (var i = 0; i < count; i++)
                {
                    var importerFormat = i < _analysisSettings.RecommendedFormats.Count ? _analysisSettings.RecommendedFormats[i] 
                        : TextureImporterFormat.ASTC_6x6;
                    newList.Add(importerFormat);
                }

                _analysisSettings.RecommendedFormats = newList;
            }

            var formats = _analysisSettings.RecommendedFormats;
            
            for (var i = 0; i < formats.Count; i++)
            {
                formats[i] = (TextureImporterFormat)EditorGUILayout.EnumPopup($"[{i}] Format: {formats[i]}", formats[i]);
            }
        }
        
        private void OnSearchPatternsSettingsGUI()
        {
            EnsureSearchPatternsLoaded();
            
            _searchPatternsSettingsFoldout = EditorGUILayout.Foldout(_searchPatternsSettingsFoldout,
                $"Search Patterns Settings. Patterns Ignored in Output: {_searchPatternsSettings.IgnoredPatterns.Count}.");

            if (!_searchPatternsSettingsFoldout) 
                return;

            EditorGUILayout.LabelField("Any changes here will be applied in the next 'Run Analysis' call", GUILayout.Width(350f));
            
            var isPatternsListDirty = false;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Format: RegExp patterns");
            if (GUILayout.Button("Set Default", GUILayout.Width(300f)))
            {
                _searchPatternsSettings.IgnoredPatterns = _searchPatternsSettings.DefaultIgnorePatterns.ToList();
                isPatternsListDirty = true;
            }

            if (GUILayout.Button("Save to Clipboard"))
            {
                var contents = _searchPatternsSettings.IgnoredPatterns.Aggregate("Patterns:", 
                    (current, t) => current + "\n" + t);

                EditorGUIUtility.systemCopyBuffer = contents;
            }
            
            EditorGUILayout.EndHorizontal();

            var newCount = Mathf.Max(0, EditorGUILayout.IntField("Count:", _searchPatternsSettings.IgnoredPatterns.Count));

            if (newCount != _searchPatternsSettings.IgnoredPatterns.Count)
            {
                isPatternsListDirty = true;
            }

            while (newCount < _searchPatternsSettings.IgnoredPatterns.Count)
            {
                _searchPatternsSettings.IgnoredPatterns.RemoveAt(_searchPatternsSettings.IgnoredPatterns.Count - 1);
            }

            if (newCount > _searchPatternsSettings.IgnoredPatterns.Count)
            {
                for (var i = _searchPatternsSettings.IgnoredPatterns.Count; i < newCount; i++)
                {
                    _searchPatternsSettings.IgnoredPatterns.Add(EditorPrefs.GetString($"{SearchPatternsSettings.PATTERNS_PREFS_KEY}_{i}"));
                }
            }

            for (var i = 0; i < _searchPatternsSettings.IgnoredPatterns.Count; i++)
            {
                var newValue = EditorGUILayout.TextField(_searchPatternsSettings.IgnoredPatterns[i]);
                if (_searchPatternsSettings.IgnoredPatterns[i] != newValue)
                {
                    isPatternsListDirty = true;
                    _searchPatternsSettings.IgnoredPatterns[i] = newValue;
                }
            }

            if (isPatternsListDirty)
            {
                SaveSearchPatterns();
            }
        }

        private void EnsureAnalysisSettingsLoaded()
        {
            // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment
            if (_analysisSettings == null)
            {
                _analysisSettings = new AnalysisSettings();
            }
        }

        private void EnsureSearchPatternsLoaded()
        {
            // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment
            if (_searchPatternsSettings == null)
            {
                _searchPatternsSettings = new SearchPatternsSettings();
            }
            
            if (_searchPatternsSettings.IgnoredPatterns != null)
            {
                return;
            }
            
            var count = EditorPrefs.GetInt(SearchPatternsSettings.PATTERNS_PREFS_KEY, -1);

            if (count == -1)
            {
                _searchPatternsSettings.IgnoredPatterns = _searchPatternsSettings.DefaultIgnorePatterns.ToList();
            }
            else
            {
                _searchPatternsSettings.IgnoredPatterns = new List<string>();
                
                for (var i = 0; i < count; i++)
                {
                    _searchPatternsSettings.IgnoredPatterns.Add(EditorPrefs.GetString($"{SearchPatternsSettings.PATTERNS_PREFS_KEY}_{i}"));
                }    
            }
        }

        private void SaveSearchPatterns()
        {
            EditorPrefs.SetInt(SearchPatternsSettings.PATTERNS_PREFS_KEY, _searchPatternsSettings.IgnoredPatterns.Count);

            for (var i = 0; i < _searchPatternsSettings.IgnoredPatterns.Count; i++)
            {
                EditorPrefs.SetString($"{SearchPatternsSettings.PATTERNS_PREFS_KEY}_{i}", _searchPatternsSettings.IgnoredPatterns[i]);
            }
        }
        
        private static void SortAtlasesByWarnings(List<AtlasData> atlases, AtlasesOutputSettings settings)
        {
            if (settings.SortType == 0)
            {
                settings.SortType = 1;
                atlases?.Sort((a, b) =>
                    b.WarningLevel.CompareTo(a.WarningLevel));
            }
            else
            {
                settings.SortType = 0;
                atlases?.Sort((a, b) =>
                    a.WarningLevel.CompareTo(b.WarningLevel));
            }
        }
        
        private static void SortAtlasesByPath(List<AtlasData> atlases, AtlasesOutputSettings settings)
        {
            if (settings.SortType == 2)
            {
                settings.SortType = 3;
                atlases?.Sort((a, b) =>
                    string.Compare(b.Path, a.Path, StringComparison.Ordinal));
            }
            else
            {
                settings.SortType = 2;
                atlases?.Sort((a, b) =>
                    string.Compare(a.Path, b.Path, StringComparison.Ordinal));
            }
        }

        private static void SortAtlasesBySpritesCount(List<AtlasData> atlases, AtlasesOutputSettings settings)
        {
            if (settings.SortType == 4)
            {
                settings.SortType = 5;
                atlases?.Sort((b, a) => a.SpritesCount.CompareTo(b.SpritesCount));
            }
            else
            {
                settings.SortType = 4;
                atlases?.Sort((a, b) => a.SpritesCount.CompareTo(b.SpritesCount));
            }
        }
        
        private static void SortTexturesByWarnings(List<TextureData> textures, TexturesOutputSettings settings)
        {
            if (settings.SortType == 0)
            {
                settings.SortType = 1;
                textures?.Sort((a, b) =>
                    b.WarningLevel.CompareTo(a.WarningLevel));
            }
            else
            {
                settings.SortType = 0;
                textures?.Sort((a, b) =>
                    a.WarningLevel.CompareTo(b.WarningLevel));
            }
        }
        
        private static void SortTexturesByPath(List<TextureData> textures, TexturesOutputSettings settings)
        {
            if (settings.SortType == 2)
            {
                settings.SortType = 3;
                textures?.Sort((a, b) =>
                    string.Compare(b.Path, a.Path, StringComparison.Ordinal));
            }
            else
            {
                settings.SortType = 2;
                textures?.Sort((a, b) =>
                    string.Compare(a.Path, b.Path, StringComparison.Ordinal));
            }
        }

        private static void SortTexturesBySize(List<TextureData> textures, TexturesOutputSettings settings)
        {
            if (settings.SortType == 4)
            {
                settings.SortType = 5;
                textures?.Sort((b, a) => a.BytesSize.CompareTo(b.BytesSize));
            }
            else
            {
                settings.SortType = 4;
                textures?.Sort((a, b) => a.BytesSize.CompareTo(b.BytesSize));
            }
        }
    }
    
    public static class GUIUtilities
    {
        private static void HorizontalLine(
            int marginTop,
            int marginBottom,
            int height,
            Color color
        )
        {
            EditorGUILayout.BeginHorizontal();
            var rect = EditorGUILayout.GetControlRect(
                false,
                height,
                new GUIStyle { margin = new RectOffset(0, 0, marginTop, marginBottom) }
            );

            EditorGUI.DrawRect(rect, color);
            EditorGUILayout.EndHorizontal();
        }

        public static void HorizontalLine(
            int marginTop = 5,
            int marginBottom = 5,
            int height = 2
        )
        {
            HorizontalLine(marginTop, marginBottom, height, new Color(0.5f, 0.5f, 0.5f, 1));
        }
    }

    public static class CommonUtilities
    {
        public static bool IsPowerOfTwo(int x)
        {
            return x != 0 && (x & (x - 1)) == 0;
        }

        public static bool IsAnyAstc(TextureImporterFormat format)
        {
            return format == TextureImporterFormat.ASTC_4x4 || format == TextureImporterFormat.ASTC_5x5 || format == TextureImporterFormat.ASTC_6x6 || format == TextureImporterFormat.ASTC_8x8 || format == TextureImporterFormat.ASTC_10x10 || format == TextureImporterFormat.ASTC_12x12;
        }
        
        public static string GetReadableTypeName(Type type)
        {
            string typeName;

            if (type != null)
            {
                typeName = type.ToString();
                typeName = typeName.Replace("UnityEngine.", string.Empty);
                typeName = typeName.Replace("UnityEditor.", string.Empty);
            }
            else
            {
                typeName = "Unknown Type";
            }

            return typeName;
        }

        public static string GetReadableSize(long bytesSize)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytesSize;
            var order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }
        
        public static bool IsAssetAddressable(string assetPath)
        {
#if HUNT_ADDRESSABLES
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var entry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(assetPath));
            return entry != null;
#else
            return false;
#endif
        }
    }
    
    internal class PocketEditorCoroutine
    {
        private readonly bool _hasOwner;
        private readonly WeakReference _ownerReference;
        private IEnumerator _routine;
        private double? _lastTimeWaitStarted;

        public static PocketEditorCoroutine Start(IEnumerator routine, EditorWindow owner = null)
        {
            return new PocketEditorCoroutine(routine, owner);
        }
        
        private PocketEditorCoroutine(IEnumerator routine, EditorWindow owner = null)
        {
            _routine = routine ?? throw new ArgumentNullException(nameof(routine));
            EditorApplication.update += OnUpdate;
            if (owner == null) return;
            _ownerReference = new WeakReference(owner);
            _hasOwner = true;
        }

        public void Stop()
        {
            EditorApplication.update -= OnUpdate;
            _routine = null;
        }
        
        private void OnUpdate()
        {
            if (_hasOwner && (_ownerReference == null || (_ownerReference != null && !_ownerReference.IsAlive)))
            {
                Stop();
                return;
            }
            
            var result = MoveNext(_routine);
            if (!result.HasValue || result.Value) return;
            Stop();
        }

        private bool? MoveNext(IEnumerator enumerator)
        {
            if (enumerator.Current is not float current) 
                return enumerator.MoveNext();
            
            _lastTimeWaitStarted ??= EditorApplication.timeSinceStartup;
            
            if (!(_lastTimeWaitStarted.Value + current
                  <= EditorApplication.timeSinceStartup))
                return null;

            _lastTimeWaitStarted = null;
            return enumerator.MoveNext();
        }
    }
}