//
// vorbis-sharp.cs - Tremolo (libvorbisidec) binding
//
// Author:
//	Atsushi Enomoto  http://d.hatena.ne.jp/atsushieno
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using C_INT = System.Int32;
#if BUILD_64
using C_LONG = System.Int64;
#else
using C_LONG = System.Int32;
#endif
using C_SIZE_T = System.Int32;
using C_VOIDPTR = System.IntPtr;
using C_CHARPTR = System.IntPtr;
using C_UCHARPTR = System.IntPtr;
using C_FILEPTR = System.IntPtr; // FILE *
using VorbisInfoPtr = System.IntPtr;
using VorbisCommentPtr = System.IntPtr;
using VorbisDspStatePtr = System.IntPtr;
using VorbisBlockPtr = System.IntPtr;
using OggPacketPtr = System.IntPtr;
using OggVorbisFilePtr = System.IntPtr;
// LAMESPEC: this definition is wrong, but it seems some devices (such as HTC Desire) doesn't
// work fine if I correctly define this.
using OV_LONG = System.Int32;

namespace UnmanagedOgg
{
	public class VorbisException : Exception
	{
		public VorbisException ()
			: base ("Vorbis exception")
		{
		}

		public VorbisException (string message)
			: base (message)
		{
		}

		public VorbisException (string message, Exception innerException)
			: base (message, innerException)
		{
		}

		public VorbisException (int errorCode)
			: base (String.Format ("Vorbis exception error code {0}", errorCode) )
		{
		}
	}

	public struct OggVorbisInfo
	{
		readonly VorbisInfo info;

		internal OggVorbisInfo (VorbisInfo info)
		{
			this.info = info;
		}

		public OggVorbisInfo (IntPtr ptr)
		{
			info = (VorbisInfo) Marshal.PtrToStructure (ptr, typeof (VorbisInfo) );
		}

		public C_INT Version { get { return info.Version; } }
		public C_INT Channels { get { return info.Channels; } }
		public C_LONG Rate { get { return info.Rate; } }
		public C_LONG BitrateUpper { get { return info.BitrateUpper; } }
		public C_LONG BitrateNominal { get { return info.BitrateNominal; } }
		public C_LONG BitrateLower { get { return info.BitrateLower; } }
		public C_LONG BitrateWindow { get { return info.BitrateWindow; } }
		// is it unsafe to expose?
		public C_VOIDPTR CodecSetup { get { return info.CodecSetup; } }
	}

	public class OggVorbisComment
	{
		readonly VorbisComment c;
		readonly Encoding text_encoding;

		internal OggVorbisComment (VorbisComment c, Encoding textEncoding)
		{
			this.c = c;
			text_encoding = textEncoding;
		}

		public OggVorbisComment (IntPtr ptr, Encoding textEncoding)
		{
			c = (VorbisComment) Marshal.PtrToStructure (ptr, typeof (VorbisComment) );
			text_encoding = textEncoding;
		}

		public string [] Comments {
			get {
				var ret = new string [c.CommentCount];
				for (int i = 0; i < ret.Length; i++)
					unsafe
					{
						var cptr = (char**) c.UserComments;
						var ptr = * (cptr + i);
						var lptr = (int*) (c.CommentLengths) + i;
						byte [] buf = new byte [*lptr];
						Marshal.Copy ( (IntPtr) ptr, buf, 0, buf.Length);
						ret [i] = text_encoding.GetString (buf);
					}
				return ret;
			}
		}

		// Unlike comments I cannot simply use text_encoding here (due to MarshalAs limitation) ...
		public string Vendor {
			get { return c.Vendor; }
		}
	}

	public class OggStreamBuffer : IDisposable
	{
		public OggStreamBuffer (string path)
			: this (path, Encoding.Default)
		{
		}

		public OggStreamBuffer (string path, Encoding textEncoding)
		{
			text_encoding = textEncoding;
			OvMarshal.ov_open (OvMarshal.fopen (path, "r") , ref vorbis_file, IntPtr.Zero, 0);
			handle_ovf = GCHandle.Alloc (vorbis_file, GCHandleType.Pinned);
			callbacks = vorbis_file.Callbacks;
		}

		public OggStreamBuffer (Stream stream)
			: this (stream, Encoding.Default)
		{
		}

		public OggStreamBuffer (Stream stream, Encoding textEncoding)
		{
			this.stream = stream;
			text_encoding = textEncoding;
			read = new OvReadFunc (Read);
			seek = new OvSeekFunc (Seek);
			close = new OvCloseFunc (Close);
			tell = new OvTellFunc (Tell);
			callbacks = new OvCallbacks (read, seek, close, tell);
			OvMarshal.ov_open_callbacks (new IntPtr (1) , ref vorbis_file, IntPtr.Zero, 0, callbacks);
			handle_ovf = GCHandle.Alloc (vorbis_file, GCHandleType.Pinned);
		}

		Stream stream;
		// those delegates have to be kept. Preserving inside OvCallbacks doesn't help (mono bug?)
		OvReadFunc read;
		OvSeekFunc seek;
		OvCloseFunc close;
		OvTellFunc tell;

		GCHandle handle_ovf;
		Encoding text_encoding;
		OvCallbacks callbacks;
		OggVorbisFile vorbis_file;
		C_LONG? bitrate_instant, streams;
		bool? seekable;
		int current_bit_stream;
		byte [] buffer;

		IntPtr vfp {
			get { return handle_ovf.AddrOfPinnedObject (); }
		}

		internal OggVorbisFile VorbisFile {
			get { return vorbis_file; }
		}

		public int CurrentBitStream {
			get { return current_bit_stream; }
		}

		public int BitrateInstant {
			get {
				if (bitrate_instant == null)
					bitrate_instant = OvMarshal.ov_bitrate_instant (vfp);
				return (int) bitrate_instant;
			}
		}

		public int Streams {
			get {
				if (streams == null)
					streams = OvMarshal.ov_streams (vfp);
				return (int) streams;
			}
		}

		public bool Seekable {
			get {
				if (seekable == null)
					seekable = OvMarshal.ov_seekable (vfp) != 0;
				return (bool) seekable;
			}
		}

		public void Dispose ()
		{
			OvMarshal.ov_clear (vfp);
			if (handle_ovf.IsAllocated)
				handle_ovf.Free ();
		}

		public int GetBitrate (int i)
		{
			C_LONG ret = OvMarshal.ov_bitrate (vfp, i);
			return (int) ret;
		}

		public int GetSerialNumber (int i)
		{
			C_LONG ret = OvMarshal.ov_serialnumber (vfp, i);
			return (int) ret;
		}

		public OggVorbisInfo GetInfo (int i)
		{
			IntPtr ret = OvMarshal.ov_info (vfp, i);
			return new OggVorbisInfo (ret);
		}

		public OggVorbisComment GetComment (int link)
		{
			IntPtr ret = OvMarshal.ov_comment (vfp, link);
			return new OggVorbisComment (ret, text_encoding);
		}

		public long GetTotalRaw (int i)
		{
			long ret = OvMarshal.ov_raw_total (vfp, i);
			return ret;
		}

		public long GetTotalPcm (int i)
		{
			long ret = OvMarshal.ov_pcm_total (vfp, i);
			return ret;
		}

		public long GetTotalTime (int i)
		{
			long ret = OvMarshal.ov_time_total (vfp, i);
			return ret;
		}

		public int SeekRaw (long pos)
		{
			int ret = OvMarshal.ov_raw_seek (vfp, pos);
			return ret;
		}

		public int SeekPcm (long pos)
		{
			int ret = OvMarshal.ov_pcm_seek (vfp, pos);
			return ret;
		}

		public int SeekTime (long pos)
		{
			int ret = OvMarshal.ov_time_seek (vfp, pos);
			return ret;
		}

		public long TellRaw ()
		{
			long ret = OvMarshal.ov_raw_tell (vfp);
			return ret;
		}

		public long TellPcm ()
		{
			long ret = OvMarshal.ov_pcm_tell (vfp);
			return ret;
		}

		public long TellTime ()
		{
			long ret = OvMarshal.ov_time_tell (vfp);
			return ret;
		}

		public long Read (short [] buffer, C_INT index, C_INT length)
		{

			return Read (buffer, index, length, ref current_bit_stream);
		}

		public long Read (short [] buffer, C_INT index, C_INT length, ref int bitStream)
		{
			long ret = 0;
			unsafe
			{
				fixed (short* bufptr = buffer)
					ret = OvMarshal.ov_read (vfp, (IntPtr) (bufptr + index) , length / 2, ref bitStream);
			}
			return ret / 2;
		}

		public long Read (byte [] buffer, C_INT index, C_INT length)
		{

			return Read (buffer, index, length, ref current_bit_stream);
		}

		public long Read (byte [] buffer, C_INT index, C_INT length, ref int bitStream)
		{
			long ret = 0;
			unsafe {
				fixed (byte* bufptr = buffer)
					ret = OvMarshal.ov_read (vfp, (IntPtr) (bufptr + index) , length, ref bitStream);
			}
			return ret;
		}

		C_SIZE_T Read (C_VOIDPTR ptr, C_SIZE_T size, C_SIZE_T nmemb, C_VOIDPTR datasource)
		{
			if (buffer == null || buffer.Length < size * nmemb)
				buffer = new byte [size * nmemb];
			var actual = (C_SIZE_T) stream.Read (buffer, 0, buffer.Length);
			if (actual < 0)
				return 0;//throw new VorbisException (String.Format ("Stream of type {0} returned a negative number: {1}", stream.GetType () , (int) actual) );
			Marshal.Copy (buffer, 0, ptr, actual);
			return actual;
		}

		C_INT Seek (C_VOIDPTR datasource, long offset, C_INT whence)
		{
			if (!stream.CanSeek)
				return -1;
			var ret = stream.Seek (offset, (SeekOrigin) whence);
			return (C_INT) ret;
		}

		C_INT Close (C_VOIDPTR datasource)
		{
			stream.Close ();
			return 0;
		}

		C_LONG Tell (C_VOIDPTR datasource)
		{
			return (C_LONG) stream.Position;
		}
	}



	#region ogg.h

	[StructLayout (LayoutKind.Sequential)]
	internal struct OggPackBuffer
	{
		public C_LONG EndByte;
		public C_INT EndBit;
		public C_UCHARPTR Buffer;
		public C_UCHARPTR Ptr;
		public C_LONG Storage;
	}

	[StructLayout (LayoutKind.Sequential)]
	internal struct OggSyncState
	{
		public C_UCHARPTR Data;
		public C_INT Storage;
		public C_INT Fill;
		public C_INT Returned;
		public C_INT Unsynced;
		public C_INT HeaderBytes;
		public C_INT BodyBytes;
	}

	[StructLayout (LayoutKind.Sequential, Size = 282)]
	internal struct OggStreamStateHeader
	{
	}

	[StructLayout (LayoutKind.Sequential, CharSet = CharSet.Ansi)]
	internal struct OggStreamState
	{
		public C_UCHARPTR BodyData;
		public C_LONG BodyStorage;
		public C_LONG BodyFill;
		public C_LONG BodyReturned;

		public IntPtr LacingValues; // int*
		public IntPtr GranuleValues; // ogg_int64_t *

		public C_LONG LacingStorage;
		public C_LONG LacingFill;
		public C_LONG LacingPacket;
		public C_LONG LacingReturned;

		// [MarshalAs (UnmanagedType.ByValTStr, SizeConst = 282)]
		//public string Header;
		public OggStreamStateHeader Header;
		public C_INT HeaderFill;

		public C_INT E_O_S;
		public C_INT B_O_S;

		public C_LONG SerialNumber;
		public C_LONG PageNumber;
		public long PacketNumber;
		public long GranulePosition;
	}

	#endregion

	#region ivorbiscodec.h

	[StructLayout (LayoutKind.Sequential)]
	internal struct VorbisInfo
	{
		public C_INT Version;
		public C_INT Channels;
		public C_LONG Rate;
		public C_LONG BitrateUpper;
		public C_LONG BitrateNominal;
		public C_LONG BitrateLower;
		public C_LONG BitrateWindow;
		public C_VOIDPTR CodecSetup;
	}

	[StructLayout (LayoutKind.Sequential)]
	internal struct VorbisDspState
	{
		public int AnalysisP;
		public IntPtr VorbisInfo; // vorbis_info *
		public IntPtr Pcm; // ogg_int32_t **
		public IntPtr PcmRet; // ogg_int32_t **
		public C_INT PcmStorage;
		public C_INT PcmCurrent;
		public C_INT PcmReturned;
		public C_INT PreExtrapolate;
		public C_INT EofFlag;
		public C_LONG LW;
		public C_LONG W;
		public C_LONG NW;
		public C_LONG CenterW;
		public long GranulePosision;
		public long Sequence;
		public C_VOIDPTR BackendState;
	}

	[StructLayout (LayoutKind.Sequential)]
	internal struct VorbisBlock
	{
		public IntPtr Pcm; // ogg_int32_t **
		public OggPackBuffer OPB;
		public C_LONG LW;
		public C_LONG W;
		public C_LONG NW;
		public C_INT PcmEnd;
		public C_INT Mode;
		public C_INT EofFlag;
		public long GranulePosision;
		public long Sequence;
		public IntPtr DspState; // vorbis_dsp_state * , read-only

		public C_VOIDPTR LocalStore;
		public C_LONG LocalTop;
		public C_LONG LocalAlloc;
		public C_LONG TotalUse;
		public IntPtr Reap; // struct alloc_chain *
	}

	[StructLayout (LayoutKind.Sequential)]
	internal struct AllocChain
	{
		public C_VOIDPTR Ptr;
		public IntPtr Next; // struct alloc_chain *
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct VorbisComment
	{
		public readonly IntPtr UserComments; // char **
		public readonly IntPtr CommentLengths; // int *
		public readonly C_INT CommentCount;
		[MarshalAs (UnmanagedType.LPStr)]
		public readonly string Vendor;
	}

	#endregion

	#region ivorbisfile.h

	internal delegate C_SIZE_T OvReadFunc (C_VOIDPTR ptr, C_SIZE_T size, C_SIZE_T nmemb, C_VOIDPTR datasource);
	internal delegate C_INT OvSeekFunc (C_VOIDPTR datasource, long offset, C_INT whence);
	internal delegate C_INT OvCloseFunc (C_VOIDPTR datasource);
	internal delegate C_LONG OvTellFunc (C_VOIDPTR datasource);

	[StructLayout (LayoutKind.Sequential)]
	internal struct OvCallbacks
	{
#if true
		public OvCallbacks (OvReadFunc read, OvSeekFunc seek, OvCloseFunc close, OvTellFunc tell)
		{
			ReadFunc = Marshal.GetFunctionPointerForDelegate (read);
			SeekFunc = Marshal.GetFunctionPointerForDelegate (seek);
			CloseFunc = Marshal.GetFunctionPointerForDelegate (close);
			TellFunc = Marshal.GetFunctionPointerForDelegate (tell);
		}

		public IntPtr ReadFunc;
		public IntPtr SeekFunc;
		public IntPtr CloseFunc;
		public IntPtr TellFunc;
#else
		public OvCallbacks (OvReadFunc read, OvSeekFunc seek, OvCloseFunc close, OvTellFunc tell)
		{
			ReadFunc = read;
			SeekFunc = seek;
			CloseFunc = close;
			TellFunc = tell;
		}
		public OvReadFunc ReadFunc;
		public OvSeekFunc SeekFunc;
		public OvCloseFunc CloseFunc;
		public OvTellFunc TellFunc;
#endif
	}

	[StructLayout (LayoutKind.Sequential)]
	internal struct OggVorbisFile
	{
		public C_VOIDPTR DataSource;
		public C_INT Seekable;
		public long Offset;
		public long End;
		public OggSyncState OY;

		public C_INT Links;
		public IntPtr Offsets; // ogg_int64_t *
		public IntPtr DataOffsets; // ogg_int64_t *
		public IntPtr SerialNumbers; // ogg_uint32_t *
		public IntPtr PcmLengths; // ogg_int64_t *
		public VorbisInfoPtr VorbisInfo;
		public VorbisCommentPtr VorbisComment;

		public long PcmOffset;
		public C_INT ReadyState;
		public uint CurrentSerialNumber;
		public C_INT CurrentLink;

		public long BitTrack;
		public long SampTrack;
		public OggStreamState StreamState;
		public VorbisDspState DspState; // central working state
		public VorbisBlock Block; // local working space

		public OvCallbacks Callbacks;
	}

	#endregion

	internal static class OvMarshal
	{
#if FULL
		const string FileLibrary = "vorbisfile";
		const string TremoloLibrary = "vorbis";
#else
		const string FileLibrary = "vorbisidec";
		const string TremoloLibrary = "vorbisidec";
#endif

		#region ivorbiscodec.h

		[DllImport (TremoloLibrary)]
		static internal extern void vorbis_info_init (ref VorbisInfo vi); // vorbis_info *, to output allocated pointer
		[DllImport (TremoloLibrary)]
		static internal extern void vorbis_info_clear (ref VorbisInfo vi); // vorbis_info *, to dealloc
		[DllImport (TremoloLibrary)]
		static internal extern C_INT vorbis_info_blocksize (VorbisInfoPtr vi, int zo);
		[DllImport (TremoloLibrary)]
		static internal extern void vorbis_comment_init (VorbisCommentPtr vc);
		[DllImport (TremoloLibrary)]
		static internal extern void vorbis_comment_add (VorbisCommentPtr vc, C_CHARPTR comment);
		[DllImport (TremoloLibrary)]
		static internal extern void vorbis_comment_add_tag (VorbisCommentPtr vc, C_CHARPTR tag, C_CHARPTR contents);
		[DllImport (TremoloLibrary)]
		static internal extern C_CHARPTR vorbis_comment_query (VorbisCommentPtr vc, C_CHARPTR tag, int count);
		[DllImport (TremoloLibrary)]
		static internal extern C_INT vorbis_comment_query_count (VorbisCommentPtr vc, C_CHARPTR tag);
		[DllImport (TremoloLibrary)]
		static internal extern void vorbis_comment_clear (VorbisCommentPtr vc);

		[DllImport (TremoloLibrary)]
		static internal extern C_INT vorbis_block_init (VorbisDspStatePtr v, VorbisBlockPtr vb);
		[DllImport (TremoloLibrary)]
		static internal extern C_INT vorbis_block_clear (VorbisBlockPtr vb);
		[DllImport (TremoloLibrary)]
		static internal extern void vorbis_dsp_clear (VorbisDspStatePtr v);

		[DllImport (TremoloLibrary)]
		static internal extern C_INT vorbis_synthesis_idheader (OggPacketPtr op);
		[DllImport (TremoloLibrary)]
		static internal extern C_INT vorbis_synthesis_headerin (VorbisInfoPtr vi, VorbisCommentPtr vc, OggPacketPtr op);

		[DllImport (TremoloLibrary)]
		static internal extern C_INT vorbis_synthesis_init (VorbisDspStatePtr v, VorbisInfoPtr vi);
		[DllImport (TremoloLibrary)]
		static internal extern C_INT vorbis_synthesis_restart (VorbisDspStatePtr v);
		[DllImport (TremoloLibrary)]
		static internal extern C_INT vorbis_synthesis (VorbisBlockPtr vb, OggPacketPtr op);
		[DllImport (TremoloLibrary)]
		static internal extern C_INT vorbis_synthesis_trackonly (VorbisBlockPtr vb, OggPacketPtr op);
		[DllImport (TremoloLibrary)]
		static internal extern C_INT vorbis_synthesis_blockin (VorbisDspStatePtr v, VorbisBlockPtr vb);
		[DllImport (TremoloLibrary)]
		static internal extern C_INT vorbis_synthesis_pcmout (VorbisDspStatePtr v, IntPtr pcm); // ogg_int32_t ***
		[DllImport (TremoloLibrary)]
		static internal extern C_INT vorbis_synthesis_read (VorbisDspStatePtr v, int samples);
		[DllImport (TremoloLibrary)]
		static internal extern C_LONG vorbis_packet_blocksize (VorbisInfoPtr vi, OggPacketPtr op);

		#endregion

		#region ivorbisfile.h

		[DllImport (FileLibrary)]
		static internal extern C_INT ov_clear (OggVorbisFilePtr vf);
		[DllImport (FileLibrary)]
		static internal extern C_INT ov_open (C_FILEPTR f, [In, Out] ref OggVorbisFile vf, C_CHARPTR initial, C_LONG ibytes);
		[DllImport (FileLibrary)]
		static internal extern C_INT ov_open_callbacks (IntPtr datasource, [In, Out] ref OggVorbisFile vf, C_CHARPTR initial, C_LONG ibytes, OvCallbacks callbacks);

		[DllImport (FileLibrary)]
		static internal extern C_INT ov_test (C_FILEPTR f, [In, Out] ref OggVorbisFile vf, C_CHARPTR initial, C_LONG ibytes);
		[DllImport (FileLibrary)]
		static internal extern C_INT ov_test_callbacks (IntPtr datasource, [In, Out] ref OggVorbisFile vf, C_CHARPTR initial, C_LONG ibytes, OvCallbacks callbacks);
		[DllImport (FileLibrary)]
		static internal extern C_INT ov_test_open ( [In, Out] ref OggVorbisFile vf);

		[DllImport (FileLibrary)]
		static internal extern C_LONG ov_bitrate (OggVorbisFilePtr vf, C_INT i);
		[DllImport (FileLibrary)]
		static internal extern C_LONG ov_bitrate_instant (OggVorbisFilePtr vf);
		[DllImport (FileLibrary)]
		static internal extern C_LONG ov_streams (OggVorbisFilePtr vf);
		[DllImport (FileLibrary)]
		static internal extern C_LONG ov_seekable (OggVorbisFilePtr vf);
		[DllImport (FileLibrary)]
		static internal extern C_LONG ov_serialnumber (OggVorbisFilePtr vf, C_INT i);

		[DllImport (FileLibrary)]
		static internal extern long ov_raw_total (OggVorbisFilePtr vf, C_INT i);
		[DllImport (FileLibrary)]
		static internal extern long ov_pcm_total (OggVorbisFilePtr vf, C_INT i);
		[DllImport (FileLibrary)]
		static internal extern long ov_time_total (OggVorbisFilePtr vf, C_INT i);

		[DllImport (FileLibrary)]
		static internal extern C_INT ov_raw_seek (OggVorbisFilePtr vf, long pos);
		[DllImport (FileLibrary)]
		static internal extern C_INT ov_pcm_seek (OggVorbisFilePtr vf, long pos);
		[DllImport (FileLibrary)]
		static internal extern C_INT ov_pcm_seek_page (OggVorbisFilePtr vf, long pos);
		[DllImport (FileLibrary)]
		static internal extern C_INT ov_time_seek (OggVorbisFilePtr vf, long pos);
		[DllImport (FileLibrary)]
		static internal extern C_INT ov_time_seek_page (OggVorbisFilePtr vf, long pos);

		[DllImport (FileLibrary)]
		static internal extern long ov_raw_tell (OggVorbisFilePtr vf);
		[DllImport (FileLibrary)]
		static internal extern long ov_pcm_tell (OggVorbisFilePtr vf);
		[DllImport (FileLibrary)]
		static internal extern long ov_time_tell (OggVorbisFilePtr vf);

		[DllImport (FileLibrary)]
		static internal extern VorbisInfoPtr ov_info (OggVorbisFilePtr vf, C_INT link);
		[DllImport (FileLibrary)]
		static internal extern VorbisCommentPtr ov_comment (OggVorbisFilePtr vf, C_INT link);

		[DllImport (FileLibrary)] // FIXME: this mismatch occurs on Desire.
		static internal extern OV_LONG ov_read (OggVorbisFilePtr vf, IntPtr buffer, C_INT length, ref int bitstream);

		[DllImport ("libc")]
		static internal extern C_FILEPTR fopen (string path, string mode);
		#endregion
	}
}
