using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

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
            // ReSharper disable once StringLiteralTypo
            public readonly List<string> DefaultIgnorePatterns = new List<string>
            {
                @"/Editor/",
                @"/Editor Default Resources/",
                @"ProjectSettings/",
                @"Packages/"
            };

            // ReSharper disable once InconsistentNaming
            public const string PATTERNS_PREFS_KEY = "CompressionHunterIgnorePatterns";

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
        }
        
        private class TexturesOutputSettings : IPaginationSettings
        {
            public int? PageToShow { get; set; } = 0;
            
            /// <summary>
            /// Sorting types.
            /// By type: 0: A-Z, 1: Z-A
            /// By path: 2: A-Z, 3: Z-A
            /// By size: 4: A-Z, 5: Z-A
            /// </summary>
            public int SortType { get; set; }
        }
        
        private interface IPaginationSettings
        {
            int? PageToShow { get; set; }
        }

        private class AtlasData
        {
            public string Path { get; }
            public Type Type { get; }
            public string TypeName { get; }
            public long BytesSize { get; }
            public string ReadableSize { get; }
            public Dictionary<string, List<TextureData>> Packables { get; }
            public bool Foldout { get; set; }
            
            public AtlasData(
                string path,
                Type type,
                string typeName,
                long bytesSize, 
                string readableSize,
                Dictionary<string, List<TextureData>> packables)
            {
                Path = path;
                Type = type;
                TypeName = typeName;
                BytesSize = bytesSize;
                ReadableSize = readableSize;
                Packables = packables;
            }
        }
        
        private class TextureData
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
            }

            public string Path { get; }
            public Type Type { get; }
            public string TypeName { get; }
            public long BytesSize { get; }
            public string ReadableSize { get; }
            public bool Foldout { get; set; }

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

            public Texture Texture
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
        
        [MenuItem("Tools/Compression Hunter")]
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
        
        private bool _analysisSettingsFoldout;
        
        private Vector2 _atlasesPagesScroll = Vector2.zero;
        private Vector2 _atlasesScroll = Vector2.zero;
        
        private Vector2 _texturesPagesScroll = Vector2.zero;
        private Vector2 _texturesScroll = Vector2.zero;
        
        private void PopulateAssetsList()
        {
            _result = new Result();
            _outputSettings = new OutputSettings();

            Clear();
            Show();
            
            EditorUtility.ClearProgressBar();

            var assetPaths = AssetDatabase.GetAllAssetPaths().ToList();
            
            var filteredOutput = new StringBuilder();
            filteredOutput.AppendLine("Assets ignored by pattern:");
            
            var count = 0;
            
            foreach (var assetPath in assetPaths)
            {
                EditorUtility.DisplayProgressBar("Textures Hunter", "Scanning for atlases",
                    (float) count / assetPaths.Count);

                var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                
                var validAssetType = type != null;

                if (!validAssetType) 
                    continue;
                
                var validForOutput = IsValidForOutput(assetPath, 
                    _analysisSettings.IgnoredPatterns);

                if (!validForOutput)
                {
                    filteredOutput.AppendLine(assetPath);
                    continue;
                }

                if (type == typeof(SpriteAtlas))
                {
                    count++;
                    _result.Atlases.Add(CreateAtlasData(assetPath));
                }
            }
            
            foreach (var assetPath in assetPaths)
            {
                EditorUtility.DisplayProgressBar("Textures Hunter", "Scanning for textures",
                    (float) count / assetPaths.Count);

                var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                
                var validAssetType = type != null;

                if (!validAssetType) 
                    continue;
                
                var validForOutput = IsValidForOutput(assetPath, 
                    _analysisSettings.IgnoredPatterns);

                if (!validForOutput)
                {
                    filteredOutput.AppendLine(assetPath);
                    continue;
                }

                if (type != typeof(SpriteAtlas))
                {
                    count++;
                }

                if (type == typeof(Texture) || type == typeof(Texture2D))
                {
                    var atlasFound = false;
                    var textureData = CreateTextureData(assetPath);

                    foreach (var atlas in _result.Atlases)
                    {
                        foreach (var packable in atlas.Packables)
                        {
                            if (assetPath.Contains(packable.Key))
                            {
                                // TODO check if in Resources or Addressable?
                                packable.Value.Add(textureData);
                                atlasFound = true;
                                break;
                            }
                        }

                        if (atlasFound)
                        {
                            break;
                        }
                    }

                    if (!atlasFound)
                    {
                        _result.Textures.Add(textureData);
                    }   
                }
                
                // TODO check if there are textures duplicates

                // TODO temp
                if (_result.Textures.Count > 300)
                {
                    break;
                }
            }
            
            _result.OutputDescription = $"Atlases: {_result.Atlases.Count}. Textures: {_result.Textures.Count}";

            SortByPath(_result.Textures, _outputSettings.TexturesSettings);

            EditorUtility.ClearProgressBar();
            
            Debug.Log(filteredOutput.ToString());
            Debug.Log(_result.OutputDescription);
            filteredOutput.Clear();

            // TODO get SpriteAtlases before textures and fill map of their textures
            // TODO fill data about SpriteAtlases
            // TODO create recommendations
            // TODO check whether in atlas
            // TODO google for recommendations of textures optimizations
            
            TextureData CreateTextureData(string path)
            {
                var fileInfo = new FileInfo(path);
                var bytesSize = fileInfo.Length;
                
                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                var typeName = CommonUtilities.GetReadableTypeName(type);
                
                return new TextureData(path, type, typeName, bytesSize, CommonUtilities.GetReadableSize(bytesSize));
            }
            
            AtlasData CreateAtlasData(string path)
            {
                var fileInfo = new FileInfo(path);
                var bytesSize = fileInfo.Length;
                
                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                var typeName = CommonUtilities.GetReadableTypeName(type);

                var atlas = EditorGUIUtility.Load(path) as SpriteAtlas;
                
                var packables = atlas.GetPackables();
                var defaultsAssets = packables.OfType<DefaultAsset>();
                var folders = defaultsAssets.Select(AssetDatabase.GetAssetPath).ToList();

                var textures = folders.ToDictionary(folder => folder, folder => new List<TextureData>());

                return new AtlasData(path, type, typeName, bytesSize, 
                    CommonUtilities.GetReadableSize(bytesSize), textures);
            }
            
            bool IsValidForOutput(string path, List<string> ignoreInOutputPatterns)
            {
                return ignoreInOutputPatterns.All(pattern 
                    => string.IsNullOrEmpty(pattern) || !Regex.Match(path, pattern).Success);
            }

        }
        
        private void OnGUI()
        {
            GUIUtilities.HorizontalLine();

            EditorGUILayout.BeginHorizontal();
            
            GUILayout.FlexibleSpace();
            
            var prevColor = GUI.color;
            GUI.color = Color.green;
            
            if (GUILayout.Button("Run Analysis", GUILayout.Width(300f)))
            {
                PopulateAssetsList();
            }
            
            GUI.color = prevColor;
            
            GUILayout.FlexibleSpace();
            
            EditorGUILayout.EndHorizontal();
            
            GUIUtilities.HorizontalLine();
            
            OnAnalysisSettingsGUI();
            
            GUIUtilities.HorizontalLine();

            if (_result == null)
            {
                return;
            }
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(_result.OutputDescription);

            EditorGUILayout.EndHorizontal();
            
            GUIUtilities.HorizontalLine();
            
            EditorGUILayout.BeginHorizontal();

            prevColor = GUI.color;
            
            var prevAlignment = GUI.skin.button.alignment;
            GUI.skin.button.alignment = TextAnchor.MiddleLeft;
            
            GUI.color = _outputSettings.TypeFilter == OutputFilterType.Textures ? Color.yellow : Color.white;
            
            if (GUILayout.Button($"[{_result.Textures.Count}] Textures", GUILayout.Width(150f)))
            {
                _outputSettings.TypeFilter = OutputFilterType.Textures;
            }
            
            GUI.color = _outputSettings.TypeFilter == OutputFilterType.Atlases ? Color.yellow : Color.white;
            
            if (GUILayout.Button($"[{_result.Atlases.Count}] Atlases", GUILayout.Width(150f)))
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

            if (_outputSettings.TypeFilter == OutputFilterType.Atlases)
            {
                OnDrawAtlases(_result.Atlases, _outputSettings.PathFilter, _outputSettings.AtlasesSettings);
            }
            else if (_outputSettings.TypeFilter == OutputFilterType.Textures)
            {
                OnDrawTextures(_result.Textures, _outputSettings.PathFilter, _outputSettings.TexturesSettings);
            }
        }

        private void OnDrawAtlases(List<AtlasData> atlases, string pathFilter, AtlasesOutputSettings settings)
        {
            if (_result.Atlases.Count == 0)
            {
                EditorGUILayout.LabelField("No atlases found");
                return;
            }
            
            var filteredAssets = atlases;
            
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

                if (GUILayout.Button(asset.Foldout ? "Minimize" : "Expand", GUILayout.Width(70)))
                {
                    asset.Foldout = !asset.Foldout;
                }
                
                EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(40f));
                
                EditorGUILayout.LabelField(asset.TypeName, GUILayout.Width(150f));    
                
                var prevColor = GUI.color;
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

                EditorGUILayout.LabelField(asset.ReadableSize, GUILayout.Width(70f));
                
                GUI.color = prevColor;
                
                EditorGUILayout.EndHorizontal();

                if (asset.Foldout)
                {
                    GUILayout.Space(3);
                    EditorGUILayout.LabelField("Path: " + asset.Path);

                    GUIUtilities.HorizontalLine();
                    
                    foreach (var packable in asset.Packables)
                    {
                        EditorGUILayout.LabelField("Folder: " + packable.Key);

                        foreach (var textureData in packable.Value)
                        {
                            // TODO add custom view
                            EditorGUILayout.LabelField(">" + textureData.Path);
                        }
                    }
                    
                    GUIUtilities.HorizontalLine();
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
            if (GUILayout.Button("Sort by type " + orderType, GUILayout.Width(150f)))
            {
                SortByType(textures, settings);
            }
        
            GUI.color = sortType == 2 || sortType == 3 ? Color.yellow : Color.white;
            orderType = sortType == 3 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by path " + orderType, GUILayout.Width(150f)))
            {
                SortByPath(textures, settings);
            }
            
            GUI.color = sortType == 4 || sortType == 5 ? Color.yellow : Color.white;
            orderType = sortType == 5 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by size " + orderType, GUILayout.Width(150f)))
            {
                SortBySize(textures, settings);
            }
            
            GUI.color = prevColor;
            
            EditorGUILayout.EndHorizontal();
            
            var filteredAssets = textures;
            
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
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(asset.Foldout ? "Minimize" : "Expand", GUILayout.Width(70)))
                {
                    asset.Foldout = !asset.Foldout;
                }
                
                EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(40f));
                
                EditorGUILayout.LabelField(asset.TypeName, GUILayout.Width(150f));    
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

                EditorGUILayout.LabelField(asset.ReadableSize, GUILayout.Width(70f));
                
                if (asset.Texture != null)
                {   
                    EditorGUILayout.LabelField($"{asset.Texture.width}x{asset.Texture.height}", GUILayout.Width(140));

                    var isPot = CommonUtilities.IsPowerOfTwo(asset.Texture.width) &&
                                CommonUtilities.IsPowerOfTwo(asset.Texture.height);
                    
                    EditorGUILayout.LabelField(isPot ? "POT" : "Non-POT", GUILayout.Width(60));

                    var isMultipleOfFour = asset.Texture.width % 4 == 0 && asset.Texture.height % 4 == 0;
                    
                    EditorGUILayout.LabelField(isMultipleOfFour ? "Multiple of 4" : "Non Multiple of 4", GUILayout.Width(120));
                }
                else
                {
                    prevColor = GUI.color;
                    GUI.color = Color.red;
                    EditorGUILayout.LabelField("Texture is null", GUILayout.Width(140));
                    GUI.color = prevColor;
                }

                DrawTextureImporter(asset.Importer);
                
                GUI.color = prevColor;
                
                EditorGUILayout.EndHorizontal();

                if (asset.Foldout)
                {
                    GUILayout.Space(3);
                    EditorGUILayout.LabelField("Path: " + asset.Path);
                    GUIUtilities.HorizontalLine();
                }
            }

            GUILayout.FlexibleSpace();
            
            EditorGUILayout.EndVertical();
            GUILayout.EndScrollView();
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

        private void DrawTextureImporter(TextureImporter importer)
        {
            var defaultSettings = importer.GetDefaultPlatformTextureSettings();
            EditorGUILayout.LabelField("Default: " + GetSettingsDescription(defaultSettings));

            var androidSettings = importer.GetPlatformTextureSettings("Android");
            if (androidSettings.overridden)
                EditorGUILayout.LabelField("Android: " + GetSettingsDescription(androidSettings));
            
            var iOSSettings = importer.GetPlatformTextureSettings("iOS");
            if (iOSSettings.overridden)
                EditorGUILayout.LabelField("iOS: " + GetSettingsDescription(iOSSettings));

            string GetSettingsDescription(TextureImporterPlatformSettings settings)
            {
                var contents = string.Empty;

                contents += settings.format.ToString();
                contents += settings.crunchedCompression ? " [Crunch]" : string.Empty;

                return contents;
            }
        }
        
        private void OnAnalysisSettingsGUI()
        {
            EnsurePatternsLoaded();
            
            _analysisSettingsFoldout = EditorGUILayout.Foldout(_analysisSettingsFoldout,
                $"Analysis Settings. Patterns Ignored in Output: {_analysisSettings.IgnoredPatterns.Count}.");

            if (!_analysisSettingsFoldout) 
                return;

            EditorGUILayout.LabelField("Any changes here will be applied to the next run", GUILayout.Width(350f));

            GUIUtilities.HorizontalLine();
            
            EditorGUILayout.LabelField("* Uncheck to list all assets with their references count", GUILayout.Width(350f));
            
            GUIUtilities.HorizontalLine();
            
            var isPatternsListDirty = false;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Format: RegExp patterns");
            if (GUILayout.Button("Set Default", GUILayout.Width(300f)))
            {
                _analysisSettings.IgnoredPatterns = _analysisSettings.DefaultIgnorePatterns.ToList();
                isPatternsListDirty = true;
            }

            if (GUILayout.Button("Save to Clipboard"))
            {
                var contents = _analysisSettings.IgnoredPatterns.Aggregate("Patterns:", 
                    (current, t) => current + "\n" + t);

                EditorGUIUtility.systemCopyBuffer = contents;
            }
            
            EditorGUILayout.EndHorizontal();

            var newCount = Mathf.Max(0, EditorGUILayout.IntField("Count:", _analysisSettings.IgnoredPatterns.Count));

            if (newCount != _analysisSettings.IgnoredPatterns.Count)
            {
                isPatternsListDirty = true;
            }

            while (newCount < _analysisSettings.IgnoredPatterns.Count)
            {
                _analysisSettings.IgnoredPatterns.RemoveAt(_analysisSettings.IgnoredPatterns.Count - 1);
            }

            if (newCount > _analysisSettings.IgnoredPatterns.Count)
            {
                for (var i = _analysisSettings.IgnoredPatterns.Count; i < newCount; i++)
                {
                    _analysisSettings.IgnoredPatterns.Add(EditorPrefs.GetString($"{AnalysisSettings.PATTERNS_PREFS_KEY}_{i}"));
                }
            }

            for (var i = 0; i < _analysisSettings.IgnoredPatterns.Count; i++)
            {
                var newValue = EditorGUILayout.TextField(_analysisSettings.IgnoredPatterns[i]);
                if (_analysisSettings.IgnoredPatterns[i] != newValue)
                {
                    isPatternsListDirty = true;
                    _analysisSettings.IgnoredPatterns[i] = newValue;
                }
            }

            if (isPatternsListDirty)
            {
                SavePatterns();
            }
        }

        private void EnsurePatternsLoaded()
        {
            // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment
            if (_analysisSettings == null)
            {
                _analysisSettings = new AnalysisSettings();
            }
            
            if (_analysisSettings.IgnoredPatterns != null)
            {
                return;
            }
            
            var count = EditorPrefs.GetInt(AnalysisSettings.PATTERNS_PREFS_KEY, -1);

            if (count == -1)
            {
                _analysisSettings.IgnoredPatterns = _analysisSettings.DefaultIgnorePatterns.ToList();
            }
            else
            {
                _analysisSettings.IgnoredPatterns = new List<string>();
                
                for (var i = 0; i < count; i++)
                {
                    _analysisSettings.IgnoredPatterns.Add(EditorPrefs.GetString($"{AnalysisSettings.PATTERNS_PREFS_KEY}_{i}"));
                }    
            }
        }

        private void SavePatterns()
        {
            EditorPrefs.SetInt(AnalysisSettings.PATTERNS_PREFS_KEY, _analysisSettings.IgnoredPatterns.Count);

            for (var i = 0; i < _analysisSettings.IgnoredPatterns.Count; i++)
            {
                EditorPrefs.SetString($"{AnalysisSettings.PATTERNS_PREFS_KEY}_{i}", _analysisSettings.IgnoredPatterns[i]);
            }
        }
        
        private static void SortByType(List<TextureData> textures, TexturesOutputSettings settings)
        {
            if (settings.SortType == 0)
            {
                settings.SortType = 1;
                textures?.Sort((a, b) =>
                    string.Compare(b.TypeName, a.TypeName, StringComparison.Ordinal));
            }
            else
            {
                settings.SortType = 0;
                textures?.Sort((a, b) =>
                    string.Compare(a.TypeName, b.TypeName, StringComparison.Ordinal));
            }
        }
        
        private static void SortByPath(List<TextureData> textures, TexturesOutputSettings settings)
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

        private static void SortBySize(List<TextureData> textures, TexturesOutputSettings settings)
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
    }
}