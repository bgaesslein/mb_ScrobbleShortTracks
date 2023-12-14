using System;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        public static MusicBeeApiInterface mbApiInterface;

        private PluginInfo about = new PluginInfo();
        private MethodInfo CallAPIMethod;
        private static readonly DateTime UnixStartTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly BindingFlags AllFlags = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.GetField
            | BindingFlags.SetField
            | BindingFlags.GetProperty
            | BindingFlags.SetProperty;
        private const double DefaultThresholdMiliseconds = 29963;
        private const double DefaultThresholdMilisecondsBeta = 29467;
        private const double ScrobbleMinSeconds = 31;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);
            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "Scrobble Short Tracks";
            about.Description = "Allows scrobbling of tracks shorter than 30 seconds.";
            about.Author = "bgaesslei";
            about.Type = PluginType.General;
            about.VersionMajor = 1;
            about.VersionMinor = 2;
            about.Revision = 0;
            about.MinInterfaceVersion = 40;
            about.MinApiRevision = 52;
            about.ReceiveNotifications = (ReceiveNotificationFlags.PlayerEvents | ReceiveNotificationFlags.TagEvents);
            about.ConfigurationPanelHeight = 0;
            return about;
        }

        public bool Configure(IntPtr panelHandle)
        {
            string dataPath = mbApiInterface.Setting_GetPersistentStoragePath();

            if (panelHandle != IntPtr.Zero)
            {
                Panel configPanel = (Panel)Panel.FromHandle(panelHandle);
                Label prompt = new Label();
                prompt.AutoSize = true;
                prompt.Location = new Point(0, 0);
                prompt.Text = "prompt:";
                TextBox textBox = new TextBox();
                textBox.Bounds = new Rectangle(60, 0, 100, textBox.Height);
                configPanel.Controls.AddRange(new Control[] { prompt, textBox });
            }
            return false;
        }

        private string GetThreshold()
        {
            if (Properties.Settings.Default.scrobbleShortTracksUserThreshold is null || Properties.Settings.Default.scrobbleShortTracksUserThreshold == "")
            {
                return GetDefaultThreshold();
            }
            else
            {
                return Properties.Settings.Default.scrobbleShortTracksUserThreshold;
            }
        }

        private string GetDefaultThreshold()
        {
            if (mbApiInterface.ApiRevision > 55)
            {
                return DefaultThresholdMilisecondsBeta.ToString();
            } else
            {
                return DefaultThresholdMiliseconds.ToString();
            }
        }

        public void SaveSettings()
        {
            string dataPath = mbApiInterface.Setting_GetPersistentStoragePath();
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                    // An unholy concoction of code to deal with obfuscation and the possibility of type and method names changing in later versions
                    var mbAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.FullName.Contains("MusicBee"));
                    foreach (var refType in mbAsm.GetTypes())
                    {
                        var method = refType.GetMethods(AllFlags).FirstOrDefault(m =>
                        {
                            var parameters = m.GetParameters();
                            return parameters.Length == 3
                                && parameters[0].ParameterType == typeof(string)
                                && parameters[2].ParameterType == typeof(KeyValuePair<string, string>[])
                                && m.ReturnType == typeof(System.Xml.XmlReader);
                        });

                        if (method != null)
                        {
                            CallAPIMethod = method;
                            break;
                        }
                    }

                    break;

                case NotificationType.TrackChanged:
                    if (mbApiInterface.NowPlaying_GetDuration() < Double.Parse(GetThreshold()))
                    {
                        MetaDataType[] fields = { MetaDataType.Artist, MetaDataType.TrackTitle, MetaDataType.Album, MetaDataType.AlbumArtist };
                        mbApiInterface.NowPlaying_GetFileTags(fields, out string[] tags);
                        ScrobbleItem(tags[0], tags[1], tags[2], tags[3], DateTime.UtcNow, ScrobbleMinSeconds );
                    }
                    break;
            }
        }

        private void ScrobbleItem(String artist, String title, String albumName, String albumArtist, DateTime startTime, double duration)
        {
            var apiCallParameters = new List<KeyValuePair<string, string>>();
            long unixTimestamp = (long)startTime.Subtract(UnixStartTime).TotalSeconds;

            apiCallParameters.Add(CreatePair("track", title));
            apiCallParameters.Add(CreatePair("artist", artist));
            apiCallParameters.Add(CreatePair("albumArtist", albumArtist));
            apiCallParameters.Add(CreatePair("album", albumName));
            apiCallParameters.Add(CreatePair("duration", duration.ToString()));
            apiCallParameters.Add(CreatePair("timestamp", unixTimestamp.ToString()));

            CallAPIMethod.Invoke(null, new object[]
            {
                "track.scrobble",
                5,
                apiCallParameters.ToArray()
            });
        }

        private KeyValuePair<string, string> CreatePair(string key, string value)
            => new KeyValuePair<string, string>(key, value);
    }
}