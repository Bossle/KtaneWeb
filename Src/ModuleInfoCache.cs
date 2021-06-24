﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RT.Json;
using RT.Serialization;
using RT.Servers;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace KtaneWeb
{
    public sealed partial class KtanePropellerModule
    {
        private sealed class ModuleInfoCache
        {
            public KtaneModuleInfo[] Modules;
            public JsonDict ModulesJson;
            public byte[] IconSpritePng;
            public string IconSpriteCss;
            public string ModuleInfoJs;
            public DateTime LastModifiedUtc;

            // Key is just the HTML filename (with extension)
            public readonly Dictionary<string, string> ManualsLastModified = new();
            public readonly Dictionary<string, string> AutogeneratedPdfs = new();
        }
        private ModuleInfoCache _moduleInfoCache;

        // This method is called in Init() (when the server is initialized) and in pull() (when the repo is updated due to a new git commit).
        private void generateModuleInfoCache()
        {
            const int cols = 40;   // number of icons per row
            const int w = 32;   // width of an icon in pixels
            const int h = 32;   // height of an icon in pixels

            var iconFiles = new DirectoryInfo(Path.Combine(_config.BaseDir, "Icons")).EnumerateFiles("*.png", SearchOption.TopDirectoryOnly).OrderBy(file => file.Name != "blank.png").ToArray();
            var rows = (iconFiles.Length + cols - 1) / cols;
            var coords = new Dictionary<string, (int x, int y)>();

            using var bmp = new Bitmap(w * cols, h * rows);
            using (var g = Graphics.FromImage(bmp))
            {
                for (int i = 0; i < iconFiles.Length; i++)
                {
                    using (var icon = new Bitmap(iconFiles[i].FullName))
                        g.DrawImage(icon, w * (i % cols), h * (i / cols));
                    coords.Add(Path.GetFileNameWithoutExtension(iconFiles[i].Name), (i % cols, i / cols));
                }
            }
            using var mem = new MemoryStream();
            bmp.Save(mem, ImageFormat.Png);

            // This needs to be a separate variable (don’t use _moduleInfoCache itself) because that field needs to stay null until it is fully initialized
            var moduleInfoCache = new ModuleInfoCache { IconSpritePng = mem.ToArray() };
            moduleInfoCache.IconSpriteCss = $".mod-icon{{background-image:url(data:image/png;base64,{Convert.ToBase64String(moduleInfoCache.IconSpritePng)})}}";

            // Load TP data from the spreadsheet
            JsonList tpEntries;
            try
            {
                tpEntries = new HClient().Get("https://spreadsheets.google.com/feeds/list/1G6hZW0RibjW7n72AkXZgDTHZ-LKj0usRkbAwxSPhcqA/1/public/values?alt=json").DataJson["feed"]["entry"].GetList();
            }
            catch (Exception e)
            {
                Log.Exception(e);
                tpEntries = new JsonList();
            }

            // Load Time Mode data from the spreadsheet
            JsonList timeModeEntries;
            try
            {
                timeModeEntries = new HClient().Get("https://spreadsheets.google.com/feeds/list/16lz2mCqRWxq__qnamgvlD0XwTuva4jIDW1VPWX49hzM/1/public/values?alt=json").DataJson["feed"]["entry"].GetList();
            }
            catch (Exception e)
            {
                Log.Exception(e);
                timeModeEntries = new JsonList();
            }

            var moduleLoadExceptions = new JsonList();
            var modules = new DirectoryInfo(Path.Combine(_config.BaseDir, "JSON"))
                .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                .ParallelSelect(Environment.ProcessorCount, file =>
                {
                    try
                    {
                        var origFile = File.ReadAllText(file.FullName);
                        var modJson = JsonDict.Parse(origFile);
                        var mod = ClassifyJson.Deserialize<KtaneModuleInfo>(modJson);

#if DEBUG
                        var newJson = (JsonDict) ClassifyJson.Serialize(mod);
                        var newJsonStr = newJson.ToStringIndented();
                        if (newJsonStr != origFile)
                            File.WriteAllText(file.FullName, newJsonStr);
                        modJson = newJson;
#endif

                        // Merge in Time Mode data
                        var timeModeEntry = timeModeEntries.FirstOrDefault(entry =>
        {
            string entryName = entry["gsx$modulename"]["$t"].GetString();
            return normalize(entryName) == normalize(mod.DisplayName ?? mod.Name);
        });

                        if (timeModeEntry != null)
                        {
                            mergeTimeModeData(mod, timeModeEntry);
                            modJson = (JsonDict) ClassifyJson.Serialize(mod);
                        }

                        // Merge in TP data
                        static string normalize(string value) => value.ToLowerInvariant().Replace('’', '\'');
                        var tpEntry = tpEntries.FirstOrDefault(entry =>
                        {
                            string entryName = entry["gsx$modulename"]["$t"].GetString();
                            return normalize(entryName) == normalize(mod.DisplayName ?? mod.Name);
                        });

                        if (tpEntry != null)
                        {
                            string scoreString = tpEntry["gsx$tpscore"]["$t"].GetString();
                            if (!string.IsNullOrEmpty(scoreString))
                            {
                                if (mod.TwitchPlays == null)
                                    mod.TwitchPlays = new KtaneTwitchPlaysInfo();

                                mod.TwitchPlays.ScoreString = scoreString;
                            }

                            modJson = (JsonDict) ClassifyJson.Serialize(mod);
                            mergeTPData(modJson, mod);
                        }

                        // Some module names contain characters that can’t be used in filenames (e.g. “?”)
                        mod.FileName = Path.GetFileNameWithoutExtension(file.Name);
                        if (mod.Name != mod.FileName)
                            modJson["FileName"] = mod.FileName;

                        if (string.IsNullOrEmpty(mod.Author) && mod.Contributors != null)
                            modJson["Author"] = mod.Contributors.ToAuthorString();

                        return (modJson, mod, file.LastWriteTimeUtc).Nullable();
                    }
                    catch (Exception e)
                    {
#if DEBUG
                        Console.WriteLine(file);
                        Console.WriteLine(e.Message);
                        Console.WriteLine(e.GetType().FullName);
                        Console.WriteLine(e.StackTrace);
#endif
                        Log.Exception(e);
                        moduleLoadExceptions.Add($"{file.Name} error: {e.Message}");
                        return null;
                    }
                })
                .WhereNotNull()
                .ToArray();

            // Process ignore lists that contain special operators
            foreach (var (modJson, mod, LastWriteTimeUtc) in modules)
                if (mod.Ignore != null && mod.Ignore.Any(str => str.StartsWith("+")))
                {
                    var processedIgnoreList = new List<string>();
                    foreach (var str in mod.Ignore)
                    {
                        if (str == "+FullBoss")
                            processedIgnoreList.AddRange(modules.Where(tup => tup.mod.IsFullBoss).Select(tup => tup.mod.DisplayName ?? tup.mod.Name));
                        else if (str == "+SemiBoss")
                            processedIgnoreList.AddRange(modules.Where(tup => tup.mod.IsSemiBoss).Select(tup => tup.mod.DisplayName ?? tup.mod.Name));
                        else if (str.StartsWith("-"))
                            processedIgnoreList.Remove(str.Substring(1));
                        else if (!str.StartsWith("+"))
                            processedIgnoreList.Add(str);
                    }
                    modJson["IgnoreProcessed"] = processedIgnoreList.ToJsonList();
                }

            moduleInfoCache.Modules = modules.Select(m => m.mod).ToArray();
            moduleInfoCache.ModulesJson = new JsonDict { { "KtaneModules", modules.Select(m => m.modJson).ToJsonList() } };
            moduleInfoCache.LastModifiedUtc = modules.Max(m => m.LastWriteTimeUtc);

            static string getFileName((JsonDict modJson, KtaneModuleInfo mod, DateTime _) tup) => tup.modJson.ContainsKey("FileName") ? tup.modJson["FileName"].GetString() : tup.mod.Name;

            var modJsons = modules.Where(tup => tup.mod.TranslationOf == null).Select(tup =>
            {
                var (modJson, mod, _) = tup;
                var fileName = getFileName(tup);
                modJson["Sheets"] = _config.EnumerateSheetUrls(fileName, modules.Select(m => m.mod.Name).Where(m => m.Length > mod.Name.Length && m.StartsWith(mod.Name)).ToArray());
                var (x, y) = coords.Get(fileName, (x: 0, y: 0));
                modJson["X"] = x;   // note how this gets set to 0,0 for icons that don’t exist, which are the coords for the blank icon
                modJson["Y"] = y;
                return modJson;
            }).ToJsonList();

            // Allow translated modules to have an icon
            foreach (var tup in modules.Where(tup => tup.mod.TranslationOf != null))
            {
                var (modJson, mod, _) = tup;
                var origModule = modules.FirstOrNull(module => module.mod.ModuleID == mod.TranslationOf);
                if (origModule == null)
                    continue;
                var fileName = getFileName(tup);
                if (!coords.ContainsKey(fileName))
                    fileName = getFileName(origModule.Value);

                var (x, y) = coords.Get(fileName, (x: 0, y: 0));
                modJson["X"] = x;
                modJson["Y"] = y;
            }

            var iconDirs = Enumerable.Range(0, _config.DocumentDirs.Length).SelectMany(ix => new[] { _config.OriginalDocumentIcons[ix], _config.ExtraDocumentIcons[ix] }).ToJsonList();
            var disps = _displays.Select(d => d.id).ToJsonList();
            var filters = _filters.Select(f => f.ToJson()).ToJsonList();
            var selectables = _selectables.Select(sel => sel.ToJson()).ToJsonList();
            var souvenir = EnumStrong.GetValues<KtaneModuleSouvenir>().ToJsonDict(val => val.ToString(), val => val.GetCustomAttribute<KtaneSouvenirInfoAttribute>().Apply(attr => new JsonDict { { "Tooltip", attr.Tooltip }, { "Char", attr.Char.ToString() } }));
            moduleInfoCache.ModuleInfoJs = $@"initializePage({modJsons},{iconDirs},{_config.DocumentDirs.ToJsonList()},{disps},{filters},{selectables},{souvenir},{moduleLoadExceptions},{File.ReadAllText(Path.Combine(_config.BaseDir, "ContactInfo.json"))});";
            _moduleInfoCache = moduleInfoCache;
        }

        private void mergeTPData(JsonDict modJson, KtaneModuleInfo mod)
        {
            // UN and T is for unchanged and temporary score which are read normally.
            string scoreString = Regex.Replace(mod.TwitchPlays.ScoreString, @"(UN|(?<=\d)T)", "");

            var parts = new List<string>();
            foreach (var factor in scoreString.SplitNoEmpty("+"))
            {
                if (factor == "TBD")
                    continue;

                var split = factor.SplitNoEmpty(" ");
                if (!split.Length.IsBetween(1, 2))
                {
                    continue;
                }

                var numberString = split[split.Length - 1];
                if (numberString.EndsWith("x")) // To parse "5x" we need to remove the x.
                    numberString = numberString.Substring(0, numberString.Length - 1);

                if (!float.TryParse(numberString, out float number))
                {
                    continue;
                }

                switch (split.Length)
                {
                    case 1:
                        parts.Add(number.Pluralize("base point"));
                        break;

                    case 2 when split[0] == "T":
                        parts.Add(number.Pluralize("point") + " per second");
                        break;

                    // D is for needy deactivations.
                    case 2 when split[0] == "D":
                        parts.Add(number.Pluralize("point") + " per deactivation");
                        break;

                    // PPA is for point per action modules which can be parsed in some cases.
                    case 2 when split[0] == "PPA":
                        parts.Add(number.Pluralize("point") + " per action");
                        break;

                    // S is for special modules which we parse out the multiplier and put it into a dictionary and use later.
                    case 2 when split[0] == "S":
                        parts.Add(number.Pluralize("point") + " per module");
                        break;
                }
            }

            modJson["TwitchPlays"]["ScoreStringDescription"] = parts.JoinString(" + ");
        }

        private void mergeTimeModeData(KtaneModuleInfo mod, JsonValue entry)
        {
            // Get score strings
            string scoreString = entry["gsx$resolvedscore"]["$t"].GetString().Trim();
            if (string.IsNullOrEmpty(scoreString))
            {
                scoreString = "10";
            }
            string scorePerModuleString = entry["gsx$resolvedbosspointspermodule"]["$t"].GetString() ?? "";

            if (mod.TimeMode == null)
                mod.TimeMode = new KtaneTimeModeInfo();

            var timeMode = mod.TimeMode;

            // Determine the score orign
            if (!string.IsNullOrEmpty(entry["gsx$assignedscore"]["$t"].GetString()))
            {
                timeMode.Origin = KtaneTimeModeOrigin.Assigned;
            }
            else if (!string.IsNullOrEmpty(entry["gsx$communityscore"]["$t"].GetString()))
            {
                timeMode.Origin = KtaneTimeModeOrigin.Community;
            }
            else if (!string.IsNullOrEmpty(entry["gsx$tpscore"]["$t"].GetString().Trim()))
            {
                timeMode.Origin = KtaneTimeModeOrigin.TwitchPlays;
            }
            else
            {
                timeMode.Origin = KtaneTimeModeOrigin.Unassigned;
            }

            // Parse scores
            if (decimal.TryParse(scoreString, out decimal score))
            {
                timeMode.Score ??= score;
            }

            if (decimal.TryParse(scorePerModuleString, out decimal scorePerModule))
            {
                timeMode.ScorePerModule ??= scorePerModule;
            }
        }
    }
}
