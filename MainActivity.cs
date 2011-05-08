﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Preferences;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Android.OS;
using UnmanagedOgg;

using Stream = System.IO.Stream;

namespace Falplayer
{
    [Activity (Label = "Falplayer", MainLauncher = true, LaunchMode = LaunchMode.SingleTask)]
    public class MainActivity : Activity
    {
        protected override void OnCreate(Bundle bundle)
        {
            base.OnCreate(bundle);

            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.Main);

            player = new Player (this);
        }
        Player player;
    }

    class PlayerView : Java.Lang.Object, SeekBar.IOnSeekBarChangeListener
    {
        const string from_history_tag = "<from history>";
        Player player;
        Activity activity;
        Button load_button, play_button, stop_button;
        TextView title_text_view;
        SeekBar seekbar;
        long loop_start, loop_length, loop_end, total_length;
        int loops;

        public PlayerView (Player player, Activity activity)
        {
            this.player = player;
            this.activity = activity;
            this.load_button = activity.FindViewById<Button>(Resource.Id.SelectButton);
            this.play_button = activity.FindViewById<Button>(Resource.Id.PlayButton);
            this.stop_button = activity.FindViewById<Button>(Resource.Id.StopButton);
            this.seekbar = activity.FindViewById<SeekBar>(Resource.Id.SongSeekbar);
            this.title_text_view = activity.FindViewById<TextView>(Resource.Id.SongTitleTextView);
            PlayerEnabled = false;

            load_button.Click += delegate {
                var db = new AlertDialog.Builder (activity);
                db.SetTitle ("Select Music Folder");
#if PREFERENCE_USABLE
                var pref = PreferenceManager.GetDefaultSharedPreferences (activity);
#if false
                var dirs = pref.GetString ("file_group", "ZAPZAPZAP!!!").Split('\\');
#else
                var dirs = new string[] { "/sdcard/falcom/ED_SORA3", "/sdcard/falcom/YSO" };
                var edit = pref.Edit();
                edit.PutString ("file_group", String.Join("\\", dirs));
                edit.Commit ();
#endif
#else
                var ifs = IsolatedStorageFile.GetUserStoreForApplication ();
                if (!ifs.FileExists ("songdirs.txt")) {
                    // FIXME: show directory-tree selector dialog and let user pick out dirs.
                    using (var sw = new StreamWriter (ifs.CreateFile ("songdirs.txt"))) {
                        sw.WriteLine ("/sdcard");
                        sw.WriteLine("/sdcard/falcom/ED_SORA3");
                        sw.WriteLine("/sdcard/falcom/YSO");
                    }
                }
                List<string> dirlist = new List<string> ();
                if (ifs.FileExists ("history.txt"))
                    dirlist.Add (from_history_tag);
                using (var sr = new StreamReader (ifs.OpenFile ("songdirs.txt", FileMode.Open)))
                    foreach (var s in sr.ReadToEnd ().Split ('\n'))
                        if (!String.IsNullOrEmpty (s))
                            dirlist.Add (s);
                var dirs = dirlist.ToArray ();
#endif
                db.SetItems (dirs, delegate (object o, DialogClickEventArgs e) {
                    string dir = dirs [(int) e.Which];
                    ProcessFileSelectionDialog (dir, delegate (string mus) {
                        player.SelectFile (mus);
                        player.Play ();
                        });
                });
                var dlg = db.Show ();
            };

            play_button.Click += delegate {
                try {
                    if (player.IsPlaying) {
                        player.Pause ();
                    } else {
                        player.Play ();
                    }
                } catch (Exception ex) {
                    play_button.Text = ex.Message;
                }
            };

            stop_button.Click += delegate {
                player.Stop ();
            };
        }

        internal void SetPlayState ()
        {
            activity.RunOnUiThread (() => play_button.Text = "Pause");
        }

        internal void SetPauseState ()
        {
            activity.RunOnUiThread (() => play_button.Text = "Play");
        }

        void ProcessFileSelectionDialog (string dir, Action<string> action)
        {
            var l = new List<string> ();
            if (dir == from_history_tag) {
                l.AddRange (player.GetPlayHistory ());
            } else {
                if (Directory.Exists (dir))
                    foreach (var file in Directory.GetFiles (dir, "*.ogg"))
                        l.Add (file);
            }
            var db = new AlertDialog.Builder(activity);
            if (l.Count == 0)
                db.SetMessage ("No music files there");
            else {
                db.SetTitle ("Select Music File");
                var files = (from f in l select Path.GetFileName (f)).ToArray ();
                db.SetItems (files, delegate (object o, DialogClickEventArgs e) {
                    int idx = (int) e.Which;
                    Android.Util.Log.Debug ("FALPLAYER", "selected song index: " + idx);
                    action (l [idx]);
                });
            }
            db.Show().Show();
        }

        public void Initialize (long totalLength, long loopStart, long loopLength, long loopEnd)
        {
            loops = 0;
            loop_start = loopStart;
            loop_length = loopLength;
            loop_end = loopEnd;
            total_length = totalLength;
            PlayerEnabled = true;
            Reset ();
        }

        public void Reset ()
        {
            activity.RunOnUiThread (delegate {
                play_button.Text = "Play";
                title_text_view.Text = string.Format ("loop: {0} - {1} - {2}", loop_start, loop_length, total_length);
                // Since our AudioTrack bitrate is fake, those markers must be faked too.
                seekbar.Max = (int) total_length;
                seekbar.Progress = 0;
                seekbar.SecondaryProgress = (int) loop_end;
                seekbar.SetOnSeekBarChangeListener (this);
                });
        }

        public bool PlayerEnabled {
            get { return play_button.Enabled; }
            set {
                activity.RunOnUiThread (delegate {
                    play_button.Enabled = value;
                    stop_button.Enabled = value;
                    seekbar.Enabled = value;
                    });
            }
        }

        public void Error (string msgbase, params object[] args)
        {
            activity.RunOnUiThread (delegate {
                PlayerEnabled = false;
                play_button.Text = String.Format(msgbase, args);
                });
        }

        public void ReportProgress (long pos)
        {
            activity.RunOnUiThread (delegate {
                title_text_view.Text = String.Format("loop: {0} / cur {1} / end {2}", loops, pos, loop_end);
                seekbar.Progress = (int) pos;
            });
        }

        public void ProcessLoop (long resetPosition)
        {
            loops++;
            seekbar.Progress = (int)resetPosition;
        }

        public void OnProgressChanged (SeekBar seekBar, int progress, bool fromUser)
        {
            if (!fromUser)
                return;
            player.Seek (progress);
        }

        public void OnStartTrackingTouch (SeekBar seekBar)
        {
            // do nothing
        }

        public void OnStopTrackingTouch (SeekBar seekBar)
        {
            // do nothing
        }
    }

    class Player
    {
        const int CompressionRate = 2;

        Activity activity;
        PlayerView view;
        OggStreamBuffer vorbis_buffer;
        LoopCommentExtension loop;
        PlayerAsyncTask task;

        public Player (Activity activity)
        {
            Initialize (activity);
        }

        void Initialize (Activity activity)
        {
            this.activity = activity;
            view = new PlayerView (this, activity);
            // "* n" part is adjusted for emulator.
            task = new PlayerAsyncTask(this);
            headset_status_receiver = new HeadphoneStatusReceiver (this);
        }

        internal string[] GetPlayHistory()
        {
            var l = new List<string>();
            var ifs = IsolatedStorageFile.GetUserStoreForApplication ();
            if (ifs.FileExists ("history.txt"))
                using (var sr = new StreamReader(ifs.OpenFile ("history.txt", FileMode.Open)))
                    foreach (var file in sr.ReadToEnd().Split ('\n'))
                        if (!String.IsNullOrEmpty(file))
                            l.Add(file);
            return l.ToArray();
        }

        public void SelectFile (string file)
        {
            Android.Util.Log.Debug ("FALPLAYER", "file to play: " + file);
            var hist = GetPlayHistory ();
            if (!hist.Contains (file)) {
                var ifs = IsolatedStorageFile.GetUserStoreForApplication ();
                using (var sw = new StreamWriter (ifs.OpenFile ("history.txt", FileMode.Append))) {
                    foreach (var h in hist.Skip (1))
                        sw.WriteLine (h);
                    sw.WriteLine (file);
                }
            }

            Stream input = File.OpenRead (file);
            vorbis_buffer = new OggStreamBuffer (input);
            loop = new LoopCommentExtension (vorbis_buffer);
            InitializeVorbisBuffer ();
        }

        public void InitializeVorbisBuffer ()
        {
            view.Initialize(loop.Total * 4, loop.Start * 4, loop.Length * 4, loop.End * 4);
            task.LoadVorbisBuffer (vorbis_buffer, loop);
        }

        public LoopCommentExtension Loop {
            get { return loop; }
        }

        public bool IsPlaying
        {
            get { return task.Status == PlayerStatus.Playing; }
        }

        HeadphoneStatusReceiver headset_status_receiver;

        public void Play ()
        {
            if (task.Status == PlayerStatus.Paused)
                task.Resume ();
            else {
                Stop ();
                task = new PlayerAsyncTask (this);
                InitializeVorbisBuffer ();
                //task.Execute ();
                task.Start ();
            }
            view.SetPlayState ();
            activity.RegisterReceiver (headset_status_receiver, new IntentFilter(AudioManager.ActionAudioBecomingNoisy));
        }

        public void Pause ()
        {
            task.Pause ();
            view.SetPauseState ();
        }

        public void Stop ()
        {
            task.Stop ();
        }

        public void Seek (long pos)
        {
            task.Seek (pos);
        }

        internal void OnComplete ()
        {
            view.Reset ();
        }

        internal void OnPlayerError (string msgbase, params object [] args)
        {
            view.Error (msgbase, args);
        }

        internal void OnProgress (long pos)
        {
            view.ReportProgress (pos);
        }

        internal void OnLoop (long resetPosition)
        {
            view.ProcessLoop (resetPosition);
        }

        enum PlayerStatus
        {
            Stopped,
            Playing,
            Paused,
        }

        class PlayerAsyncTask //: AsyncTask
        {
            static readonly int min_buf_size = AudioTrack.GetMinBufferSize(44100 / CompressionRate * 2, (int)ChannelConfiguration.Stereo, Encoding.Pcm16bit);
            int buf_size = min_buf_size * 8;

            AudioTrack audio;
            Player player;
            bool pause, finish;
            AutoResetEvent pause_handle = new AutoResetEvent (false);
            int x;
            byte [] buffer;
            long loop_start, loop_length, loop_end, total;
            Thread player_thread;

            public PlayerAsyncTask (Player player)
            {
                this.player = player;
                audio = new AudioTrack (Android.Media.Stream.Music, 44100 / CompressionRate * 2, ChannelConfiguration.Stereo, Android.Media.Encoding.Pcm16bit, buf_size * 2, AudioTrackMode.Stream);
                buffer = new byte [buf_size / 2 / CompressionRate];
                player_thread = new Thread (() => DoRun ());
            }

            public PlayerStatus Status { get; private set; }

            public void LoadVorbisBuffer (OggStreamBuffer ovb, LoopCommentExtension loop)
            {
                loop_start = loop.Start * 4;
                loop_length = loop.Length * 4;
                loop_end = loop.End * 4;
                total = loop.Total;
            }

            public void Pause ()
            {
                Status = PlayerStatus.Paused;
                pause = true;
            }

            public void Resume ()
            {
                Status = PlayerStatus.Playing;
                pause = false; // make sure to not get overwritten
                pause_handle.Set ();
            }

            public void Seek (long pos)
            {
                if (pos < 0 || pos >= loop_end) 
                    return; // ignore
                var prevStat = Status;
                if (prevStat == PlayerStatus.Playing)
                    Pause ();
                audio.Flush ();
                SpinWait.SpinUntil(() => !pause);
                total = pos;
                player.vorbis_buffer.SeekPcm (pos / 4);
                if (prevStat == PlayerStatus.Playing)
                    Resume ();
            }

            public void Stop ()
            {
                finish = true; // and let player loop finish.
                pause_handle.Set ();
                Status = PlayerStatus.Stopped;
            }

            public void Start ()
            {
                player_thread.Start ();
            }
            /*
            protected override Java.Lang.Object DoInBackground(params Java.Lang.Object[] @params)
            {
                return DoRun();
            }
            */

            Java.Lang.Object DoRun()
            {
                player.vorbis_buffer.SeekRaw (0);
                Status = PlayerStatus.Playing;
                x = 0;
                total = 0;

                audio.Play ();
                while (!finish)
                {
                    if (pause) {
                        pause = false;
                        audio.Pause ();
                        pause_handle.WaitOne ();
                        audio.Play ();
                    }
                    long ret = player.vorbis_buffer.Read (buffer, 0, buffer.Length);
                    if (ret <= 0 || ret > buffer.Length) {
                        finish = true;
                        if (ret < 0)
                            player.OnPlayerError ("vorbis error : {0}", ret);
                        else if (ret > buffer.Length)
                            player.OnPlayerError ("buffer overflow : {0}", ret);
                        break;
                    }

                    if (ret + total >= loop_end)
                        ret = loop_end - total; // cut down the buffer after loop
                    total += ret;

                    if (++x % 50 == 0)
                        player.OnProgress (total);

                    // downgrade bitrate
                    for (int i = 1; i < ret * 2 / CompressionRate; i++)
                        buffer[i] = buffer[i * CompressionRate / 2 + (CompressionRate / 2) - 1];
                    audio.Write (buffer, 0, (int)ret * 2 / CompressionRate);
                    // loop back to LOOPSTART
                    if (total >= loop_end)
                    {
                        player.OnLoop (loop_start);
                        player.vorbis_buffer.SeekPcm (loop_start / 4); // also faked
                        total = loop_start;
                    }
                }
                audio.Flush ();
                audio.Stop ();
                player.OnComplete ();
                return null;
            }
        }
    }

    class HeadphoneStatusReceiver : BroadcastReceiver
    {
        Player player;
        public HeadphoneStatusReceiver (Player player)
        {
            this.player = player;
        }

        public override void OnReceive (Context context, Intent intent)
        {
            if (intent.Action == AudioManager.ActionAudioBecomingNoisy)
                player.Pause ();
        }
    }

    public class LoopCommentExtension
    {
        long loop_start = 0, loop_length = int.MaxValue, loop_end = int.MaxValue, total;

        public LoopCommentExtension (OggStreamBuffer owner)
        {
            total = owner.GetTotalPcm (-1);
            foreach (var cmt in owner.GetComment(-1).Comments)
            {
                var comment = cmt.Replace(" ", ""); // trim spaces
                if (comment.StartsWith("LOOPSTART="))
                    loop_start = int.Parse(comment.Substring("LOOPSTART=".Length));
                if (comment.StartsWith("LOOPLENGTH="))
                    loop_length = int.Parse(comment.Substring("LOOPLENGTH=".Length));
            }

            if (loop_start > 0 && loop_length > 0)
                loop_end = (loop_start + loop_length);
        }

        public long Start {
            get { return loop_start; }
        }

        public long Length {
            get { return loop_length; }
        }

        public long End {
            get { return loop_end; }
        }

        public long Total
        {
            get { return total; }
        }
    }
}
