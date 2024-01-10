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
        private TextBox thresholdTextbox;
        private string previousPlaycount;
        private double previousDurationMiliseconds;

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
            about.ConfigurationPanelHeight = 75;
            return about;
        }

        public bool Configure(IntPtr panelHandle)
        {
            if (panelHandle != IntPtr.Zero)
            {
                Panel configPanel = (Panel)Panel.FromHandle(panelHandle);
                Label thresholdLabel = new Label();
                thresholdLabel.AutoSize = true;
                thresholdLabel.Location = new Point(0, 0 + 2);
                thresholdLabel.Text = "Scrobble Threshold(ms):";

                thresholdTextbox = new TextBox();
                thresholdTextbox.Location = new Point(thresholdLabel.Width + 35, 0 );
                thresholdTextbox.Width = 120;
                thresholdTextbox.Text = GetThreshold();
                thresholdTextbox.BackColor = Color.FromArgb(mbApiInterface.Setting_GetSkinElementColour(SkinElement.SkinInputControl,
                                                                                                        ElementState.ElementStateDefault,
                                                                                                        ElementComponent.ComponentBackground));
                thresholdTextbox.ForeColor = Color.FromArgb(mbApiInterface.Setting_GetSkinElementColour(SkinElement.SkinInputControl,
                                                                                                        ElementState.ElementStateDefault,
                                                                                                        ElementComponent.ComponentForeground));
                thresholdTextbox.BorderStyle = BorderStyle.FixedSingle;

                Button resetButton = new Button();
                resetButton.FlatAppearance.BorderColor = Color.FromArgb(mbApiInterface.Setting_GetSkinElementColour(SkinElement.SkinInputControl, ElementState.ElementStateDefault, ElementComponent.ComponentBorder));
                resetButton.Font = mbApiInterface.Setting_GetDefaultFont();
                resetButton.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
                resetButton.Text = "Set default";
                resetButton.Click += ResetButton_Click;
                resetButton.Location = new Point(thresholdTextbox.Location.X + thresholdTextbox.Width + 5, 0); 

                Label thresholdInfo = new Label();
                thresholdInfo.Location = new Point(0, thresholdTextbox.Height + 8);
                thresholdInfo.MaximumSize = new System.Drawing.Size(480, 0);
                thresholdInfo.Text = $"Default threshold is {GetDefaultThreshold():n0}ms. The built-in last.fm plugin should scrobble any song longer than this. Unfortunately, this seems to be inconsistent so if you're still experiencing double scrobbles, try lowering the threshold.";
                thresholdInfo.AutoSize = true;

                configPanel.Controls.AddRange(new Control[] { thresholdInfo, thresholdLabel, thresholdTextbox, resetButton });
            }
            return false;
        }

        private void ResetButton_Click(object sender, EventArgs e)
        {
            if (thresholdTextbox is null)
            {   
                return;
            }
            thresholdTextbox.Text = GetDefaultThreshold();
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
            Properties.Settings.Default.scrobbleShortTracksUserThreshold = thresholdTextbox.Text == GetDefaultThreshold() ? "" : thresholdTextbox.Text;
            Properties.Settings.Default.Save();
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                    previousPlaycount = mbApiInterface.NowPlaying_GetFileProperty(FilePropertyType.PlayCount);
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
                    previousPlaycount = mbApiInterface.NowPlaying_GetFileProperty(FilePropertyType.PlayCount);
                    previousDurationMiliseconds = mbApiInterface.NowPlaying_GetDuration();
                    break;

                case NotificationType.PlayCountersChanged:
                    if (previousDurationMiliseconds < Double.Parse(GetThreshold()) && !(previousPlaycount == mbApiInterface.Library_GetFileProperty(sourceFileUrl, FilePropertyType.PlayCount)))
                    {
                        MetaDataType[] fields = { MetaDataType.Artist, MetaDataType.TrackTitle, MetaDataType.Album, MetaDataType.AlbumArtist };
                        mbApiInterface.NowPlaying_GetFileTags(fields, out string[] tags);
                        ScrobbleItem(tags[0], tags[1], tags[2], tags[3], DateTime.UtcNow, ScrobbleMinSeconds);
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