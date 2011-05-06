using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

namespace Falplayer
{
    [Activity (Label = "Select Music Folder")]
    public class FileGroupsSelectorActivity : Activity
    {
        protected override void OnCreate(Bundle bundle)
        {
            base.OnCreate(bundle);

            // Create your application here
            SetContentView (Resource.Layout.FileGroupSelector);

            var pref = GetSharedPreferences ("file_group", FileCreationMode.WorldWriteable);
            var dirs = new string [] {"/falcom/ED_SORA3", "/falcom/YSO"};// pref.GetString ("groups", String.Empty).Split ('\n');
            var edit = pref.Edit();
            edit.Remove ("file_group");
            edit.PutString ("file_group", String.Join("\n", dirs));
            edit.Commit ();
            var arp = new ArrayAdapter<string>(this, Resource.Layout.FileGroupSelectorListItem, dirs);
            var lv = this.FindViewById<ListView>(Resource.Id.GroupListView);
            lv.Adapter = arp;

            lv.ItemClick += delegate (object o, ItemEventArgs e) {
                Android.Util.Log.Debug ("FALPLAYER", "selected directory: " + dirs [e.Position]);
                this.SetResult (Result.Ok);
            };

            var cancel = this.FindViewById<Button> (Resource.Id.CancelButton);
            cancel.Click += delegate {
                this.SetResult(Result.Canceled);
            };
        }
    }
}