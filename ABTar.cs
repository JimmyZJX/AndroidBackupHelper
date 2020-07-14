using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace AndroidBackupHelper {

    /// <summary>
    /// Implemented based on Android source code
    /// frameworks/base/libs/androidfw/BackupHelpers
    /// </summary>
    public class ABTar {

        public const int BUFSIZE = 32 * 1024;

        public static void strcat(byte[] dest, int offset, string src, bool writeNULL = true) {
            var srcBytes = Encoding.UTF8.GetBytes(src);
            Buffer.BlockCopy(srcBytes, 0, dest, offset, srcBytes.Length);

            if (writeNULL) {
                dest[offset + srcBytes.Length] = 0;
            }
        }

        public static void bytesNcpy(byte[] dest, int doffset, byte[] src, int soffset, int maxBytes) {
            int bytes = Math.Min(maxBytes, src.Length - soffset);
            Trace.Assert(dest.Length - doffset > bytes, "bytesNcpy: insufficient destination length");
            Buffer.BlockCopy(src, soffset, dest, doffset, bytes);
        }


        // Returns number of bytes written
        static int write_pax_header_entry(byte[] buf, int offset, string key, string value) {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var valueBytes = Encoding.UTF8.GetBytes(value);

            // start with the size of "1 key=value\n"
            int len = keyBytes.Length + valueBytes.Length + 4;
            if (len > 9) len++;
            if (len > 99) len++;
            if (len > 999) len++;
            // since PATH_MAX is 4096 we don't expect to have to generate any single
            // header entry longer than 9999 characters

            var content = $"{len} {key}={value}\n";
            strcat(buf, offset, content);
            return Encoding.UTF8.GetBytes(content).Length;
            // return sprintf(buf, "%d %s=%s\n", len, key, value);
        }

        static string ToOctalLeadingZero(long num, int zeros) {
            string leading = new string('0', zeros);
            string str = Convert.ToString(num, 8);
            string leading_str = leading + str;
            return leading_str.Substring(leading_str.Length - zeros);
        }

        static void calc_tar_checksum(byte[] buf, int offset) {
            // [ 148 :   8 ] checksum -- to be calculated with this field as space chars
            for (int i = 0; i < 8; i++) {
                buf[offset + 148 + i] = Convert.ToByte(' ');
            }

            var sum = (UInt16)Enumerable.Range(offset, 512).Sum(p => buf[p]);

            // Now write the real checksum value:
            // [ 148 :   8 ]  checksum: 6 octal digits [leading zeroes], NUL, SPC
            strcat(buf, offset + 148, ToOctalLeadingZero(sum, 6)); // the trailing space is already in place
        }

        static void send_tarfile_chunk(Stream tar, byte[] buffer, int offset, int size) {
            if (size < 0) size = buffer.Length;

            long chunk_size_no = IPAddress.HostToNetworkOrder(size); // htonl
            // tar.Write(BitConverter.GetBytes(chunk_size_no), 0, 4); // why writing an additional int?
            if (size != 0) tar.Write(buffer, 0, size);
        }

        public static void FinishTar(Stream tar) {
            send_tarfile_chunk(tar, new byte[1024], 0, 1024);
        }

        // [apps/{pkgName}/{domain}/]relPath
        public static void WriteTarFile(string packageName, string domain, string relPath, FileStream file, Stream tar, out long outSize) {

            bool isDir = false; long fileSize = 0;
            if (file == null) {
                isDir = true;
            } else {
                Trace.Assert(file.CanRead, $"[-] WriteTarFile: File '{relPath}' not readable");
                fileSize = file.Length;
            }

            Trace.Assert(tar.CanWrite, "[-] WriteTarFile: tar not writeable");

            // Too long a name for the ustar format?
            //    "apps/" + packagename + '/' + domainpath < 155 chars
            //    relpath < 100 chars
            bool needExtended =
                (5 + packageName.Length + 1 + domain.Length >= 155)
                || relPath.Length >= 100;

            needExtended |=
                Encoding.UTF8.GetBytes(relPath).Any(c => (c & 0x80) != 0);

            needExtended |= fileSize > 077777777777L;

            string fullname, prefix;

            // Report the size, including a rough tar overhead estimation: 512 bytes for the
            // overall tar file-block header, plus 2 blocks if using the pax extended format,
            // plus the raw content size rounded up to a multiple of 512.
            outSize = 512 + (needExtended ? 1024 : 0) + 512 * ((fileSize + 511) / 512);

            byte[] buf = new byte[BUFSIZE];
            int paxHeader_offset = 512, paxData_offset = 1024;

            // Magic fields for the ustar file format
            strcat(buf, 257, "ustar");
            strcat(buf, 263, "00");

            // [ 265 : 32 ] user name, ignored on restore
            // [ 297 : 32 ] group name, ignored on restore

            // [ 100 :   8 ] file mode
            // snprintf(buf + 100, 8, "%06o ", s.st_mode & ~S_IFMT);
            strcat(buf, 100, "000755 ");

            // [ 108 :   8 ] uid -- ignored in Android format; uids are remapped at restore time
            // [ 116 :   8 ] gid -- ignored in Android format
            // snprintf(buf + 108, 8, "0%lo", (unsigned long)s.st_uid);
            // snprintf(buf + 116, 8, "0%lo", (unsigned long)s.st_gid);

            // [ 124 :  12 ] file size in bytes
            // snprintf(buf + 124, 12, "%011llo", (isdir) ? 0LL : s.st_size);
            strcat(buf, 124, ToOctalLeadingZero(fileSize, 11));

            // [ 136 :  12 ] last mod time as a UTC time_t
            // snprintf(buf + 136, 12, "%0lo", (unsigned long)s.st_mtime);
            strcat(buf, 136, ToOctalLeadingZero(DateTime.UtcNow.ToFileTimeUtc(), 11));

            // [ 156 :   1 ] link/file type
            byte type;
            if (isDir) {
                type = Convert.ToByte('5');     // tar magic: '5' == directory
            } else {
                type = Convert.ToByte('0');     // tar magic: '0' == normal file
            }
            buf[156] = type;

            // [ 157 : 100 ] name of linked file [not implemented]

            {
                // Prefix and main relative path.  Path lengths have been preflighted.
                prefix = "apps/";
                if (packageName.Length > 0) {
                    prefix += packageName;
                }
                if (domain.Length > 0) {
                    prefix += "/" + domain;
                }

                // pax extended means we don't put in a prefix field, and put a different
                // string in the basic name field.  We can also construct the full path name
                // out of the substrings we've now built.
                fullname = prefix;
                fullname += "/" + relPath;

                // ustar:
                //    [   0 : 100 ]; file name/path
                //    [ 345 : 155 ] filename path prefix
                // We only use the prefix area if fullname won't fit in the path
                var fullnameBytes = Encoding.UTF8.GetBytes(fullname);
                if (fullnameBytes.Length > 100) {
                    bytesNcpy(buf, 0, Encoding.UTF8.GetBytes(relPath), 0, 100);
                    bytesNcpy(buf, 345, Encoding.UTF8.GetBytes(prefix), 0, 155);
                } else {
                    bytesNcpy(buf, 0, fullnameBytes, 0, 100);
                }
            }

            // [ 329 : 8 ] and [ 337 : 8 ] devmajor/devminor, not used

            Debug.WriteLine($"   Name: {fullname}");

            // If we're using a pax extended header, build & write that here; lengths are
            // already preflighted
            if (needExtended) {
                int p = paxData_offset;

                // construct the pax extended header data block
                // memset(paxData, 0, BUFSIZE - (paxData - buf));

                // size header -- calc len in digits by actually rendering the number
                // to a string - brute force but simple
                p += write_pax_header_entry(buf, p, "size", fileSize.ToString());

                // fullname was generated above with the ustar paths
                p += write_pax_header_entry(buf, p, "path", fullname);

                // Now we know how big the pax data is
                int paxLen = p - paxData_offset;

                // Now build the pax *header* templated on the ustar header
                // memcpy(paxHeader, buf, 512);
                bytesNcpy(buf, paxHeader_offset, buf, 0, 512);

                var leaf = Path.GetFileName(fullname);
                // rewrite the name area
                bytesNcpy(buf, paxHeader_offset, new byte[100], 0, 100);
                strcat(buf, paxHeader_offset, $"PaxHeader/{leaf}".Substring(0, 99));

                // rewrite the prefix area
                bytesNcpy(buf, paxHeader_offset + 345, new byte[155], 0, 155);
                strcat(buf, paxHeader_offset + 345, prefix.Substring(0, 154));

                buf[paxHeader_offset + 156] = Convert.ToByte('x'); // mark it as a pax extended header

                // [ 124 :  12 ] size of pax extended header data
                bytesNcpy(buf, paxHeader_offset + 124, new byte[12], 0, 12);
                strcat(buf, paxHeader_offset + 124, ToOctalLeadingZero(paxLen, 11));

                // Checksum and write the pax block header
                calc_tar_checksum(buf, paxHeader_offset);
                send_tarfile_chunk(tar, buf, paxHeader_offset, 512);

                // Now write the pax data itself
                int paxblocks = (paxLen + 511) / 512;
                send_tarfile_chunk(tar, buf, paxData_offset, 512 * paxblocks);
            }

            // Checksum and write the 512-byte ustar file header block to the output
            calc_tar_checksum(buf, 0);
            send_tarfile_chunk(tar, buf, 0, 512);

            // Now write the file data itself, for real files.  We honor tar's convention that
            // only full 512-byte blocks are sent to write().
            if (!isDir && fileSize > 0) {
                file.Seek(0, SeekOrigin.Begin);
                long toWrite = file.Length;
                while (toWrite > 0) {
                    var toRead = toWrite;
                    if (toRead > BUFSIZE) {
                        toRead = BUFSIZE;
                    }
                    var nRead = file.Read(buf, 0, (int)toRead);
                    if (nRead < 0) {
                        Trace.TraceError($"Unable to read file (read returns {nRead})");
                        break;
                    } else if (nRead == 0) {
                        Trace.TraceError($"EOF but expect {toWrite} more bytes");
                        break;
                    }

                    // At EOF we might have a short block; NUL-pad that to a 512-byte multiple.  This
                    // depends on the OS guarantee that for ordinary files, read() will never return
                    // less than the number of bytes requested.
                    int partial = (nRead + 512) % 512;
                    if (partial > 0) {
                        var remainder = 512 - partial;
                        for (int p = 0; p < remainder; p++) {
                            buf[nRead++] = 0;
                        }
                    }
                    send_tarfile_chunk(tar, buf, 0, nRead);
                    toWrite -= nRead;
                }
            }
        }

    }
}
