﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ConsoleApplication2.Data.Mxe;
using System.IO;
using ValkyrieEdit.Discover;
using ValkyrieEdit.Reader;

namespace ConsoleApplication2.Reader
{

    public class MxeParser : Parser
    {
        private const string CSV_OPEN_NAME_ERROR = @"Csv file [{0}] was not named in the expect manner (e.g. '4-VlMxMapObjectCommonInfo-Data.csv'). Skipping file.";
        private const string CSV_MATCH_TITLE_ERROR = @"Mxe type mismatch referenced in file name [{0}] on line [{1}]. Expected [{2}] but found [{3}]. Skipping record.";
        private const string CSV_MATCH_SIZE_ERROR = @"Mxe size mismatch referenced in file name [{0}] on line [{1}]. Expected [{2}] but found [{3}]. Skipping record.";
        private const string CSV_MATCH_COUNT_ERROR = @"Mxe count mismatch referenced in file name [{0}] on line [{1}]. Expected [{2}] but found [{3}]. Skipping record.";
        private const string ALL_INDEX_FORMAT = @"{0}\All-Indexes.csv";
        private const string CSV_FILE_FORMAT = @"{0}\{1}-{2}{3}";
        private const string CSV_ENDING = @"-Data.csv";
        private const string CSV_INDEX_ENDING = @"-Index.csv";
        private const string CSV_HEX_ENDING = @"-Hex.csv";

        protected const int _tableCountAddr = 0x84;
        protected const int _tableStartAddr = 0x88;

        protected ByteWord _tableCount;
        protected ByteWord _tableStart;

        protected Dictionary<int, MxeIndexEntry> _indexes;

        public MxeParser(String filename)
        {
            _indexes = new Dictionary<int, MxeIndexEntry>();
            _filename = filename;
            _basedir = @".\" + Path.GetFileNameWithoutExtension(_filename);
            Directory.CreateDirectory(_basedir);
        }

        protected override void ReadTableMeta(FileStream stream)
        {
            _tableCount = new ByteWord(_tableCountAddr);
            _tableStart = new ByteWord(_tableStartAddr);

            _tableCount.ReadFromFile(stream);
            _tableStart.ReadFromFile(stream);

            int current = BitConverter.ToInt32( _tableStart.GetBytes(), 0 );
            int count = BitConverter.ToInt32( _tableCount.GetBytes(), 0 );

            Console.Out.WriteLine("Entry count: " + count);

            for (int i = 0; i < count; i++)
            {
                MxeIndexEntry e = new MxeIndexEntry(GetRealAddress(current));
                e.ReadEntry(stream);
                _indexes.Add(e.GetIndex(), e);

                current += _tableStart.Length * 4;
            }

            Dictionary<string, MxeEntryType> dts = DiscoverTypes(stream);

            RefreshOtherTypesToDiscoveredType(dts);
        }

        private void RefreshOtherTypesToDiscoveredType(Dictionary<string, MxeEntryType> dts)
        {
            foreach (MxeIndexEntry mie in _indexes.Values)
            {
                string key = mie.GetVmTitle();
                if (mie.Block.Type == MxeEntryType.Other && dts.ContainsKey(key))
                {
                    mie.Block.Type = dts[key];
                }
            }
        }

        private Dictionary<string, MxeEntryType> DiscoverTypes(FileStream stream)
        {
            Dictionary<string, List<string>> discoveredTypes = new Dictionary<string, List<string>>();
            foreach (MxeIndexEntry mie in _indexes.Values)
            {
                if (mie.Block.Type == MxeEntryType.Other)
                {
                    string key = mie.GetVmTitle();
                    List<string> currTypes = mie.SuggestTypes(stream);
                    if (!discoveredTypes.ContainsKey(key))
                    {
                        discoveredTypes[key] = currTypes;
                    }
                    else
                    {
                        MergeTypeLists(discoveredTypes[key], currTypes);
                    }
                }
            }

            Dictionary<string, MxeEntryType> dts = new Dictionary<string, MxeEntryType>();
            foreach (KeyValuePair<string, List<string>> entry in discoveredTypes)
            {
                MxeEntryType newType = new MxeEntryType(entry.Key, entry.Value);
                MxeEntryType.KnownTypes.Add(entry.Key, newType);
                dts.Add(entry.Key, newType);
                ConfigDiscovery.AddNewMxeType(newType);
            }

            return dts;
        }

        private List<string> MergeTypeLists(List<string> old, List<string> news)
        {
            if (old == null)
            {
                return news;
            }

            for (int i = 0; i < news.Count; i++)
            {
                if (i >= old.Count)
                {
                    old.Add(news[i]);
                }
                else if (old[i] == news[i])
                {
                    continue;
                }
                else if (news[i] == "h" || news[i] == "l" || news[i] == "b")
                {
                    old[i] = "h";
                }
                else if ((old[i] == "i" && news[i] == "f") || (old[i] == "f" && news[i] == "i"))
                {
                    old[i] = "f";
                }
                else if ((old[i] == "i" && news[i] == "p") || (old[i] == "p" && news[i] == "i"))
                {
                    old[i] = "i";
                }
            }

            return old;
        }

        public override void Write()
        {
            try
            {
                using (var stream = new FileStream(_filename, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    foreach (MxeIndexEntry mie in _indexes.Values)
                    {
                        mie.WriteToMxe(stream);
                    }

                    stream.Close();
                }
            }
            catch (Exception exc)
            {
                Console.Out.WriteLine(exc.ToString());
            }
            
        }

        public override void WriteIndexes()
        {
            WriteCsvFile(_indexes.Values.ToList(), String.Format(ALL_INDEX_FORMAT, _basedir), true);
            WriteFilesByGroup(CSV_FILE_FORMAT, CSV_INDEX_ENDING, true);
        }

        public override void WriteCsv()
        {
            if (MxeWord.Hex)
            {
                WriteFilesByGroup(CSV_FILE_FORMAT, CSV_HEX_ENDING, false);
            }
            else
            {
                WriteFilesByGroup(CSV_FILE_FORMAT, CSV_ENDING, false);
            }
        }

        protected void WriteFilesByGroup(string fnFormat, string fext, bool isIndex)
        {
            foreach (IGrouping<string, MxeIndexEntry> group in _indexes.Values.GroupBy(x => x.GetVmTitle()))
            {
                List<MxeIndexEntry> grouplist = group.ToList<MxeIndexEntry>();
                if (grouplist[0].Block != null)
                {
                    string name = group.Key;
                    int val = BitConverter.ToInt32(grouplist[0].TypeCode.GetBytes(), 0);
                    string ext = fext;
                    if (grouplist[0].Block.Type == MxeEntryType.Other)
                    {
                        ext = "-OTHER" + ext;
                    }
                    WriteCsvFile(grouplist, String.Format(fnFormat, _basedir, val, name, ext), isIndex);
                }
            }
        }

        protected void WriteCsvFile(List<MxeIndexEntry> grouplist, string fn, bool isIndex)
        {
            String msg = isIndex ? "Writing index file " : "Writing data file ";
            Console.Out.WriteLine(msg + fn);
            try
            {
                using (var stream = new StreamWriter(fn))
                {
                    if(isIndex)
                    {
                        WriteCsvIndexes(stream, grouplist);
                    }
                    else
                    {
                        WriteCsvItems(stream, grouplist);
                    }
                    
                    stream.Close();
                }
            }
            catch (Exception exc)
            {
                Console.Out.WriteLine(exc.ToString());
            }
        }

        protected void WriteCsvItems(StreamWriter stream, List<MxeIndexEntry> group)
        {
            stream.Write("iMxeIndex,iMxeVm,");
            foreach (string h in group[0].Block.Type.Headers)
            {
                if (!String.IsNullOrEmpty(h) || MxeWord.Verbose)
                {
                    if (h.StartsWith("p") && MxeWord.Literal)
                    {
                        stream.Write('h');
                    }
                    else if (MxeWord.Hex)
                    {
                        stream.Write('h');
                    }

                    stream.Write(h);
                    stream.Write(',');
                }
            }

            stream.Write(stream.NewLine);

            foreach (MxeIndexEntry mie in group)
            {
                mie.WriteCsv( stream );
                stream.Write(stream.NewLine);
            }
        }

        protected void WriteCsvIndexes(StreamWriter stream, List<MxeIndexEntry> group)
        {
            stream.Write("iIndex,pVm,iType,pAddr");
            stream.Write(stream.NewLine);

            group.Sort((x, y) => x.Position.CompareTo(y.Position));
            foreach (MxeIndexEntry mie in group)
            {
                mie.WriteIndex(stream);
                stream.Write(stream.NewLine);
            }
        }

        public override bool ReadCsvs()
        {
            DirectoryInfo d = new DirectoryInfo(@".\" + _basedir);
            FileInfo[] files;
            bool foundAChange = false;
            string search = "*" + CSV_ENDING;
            if (MxeWord.Hex)
            {
                search = "*" + CSV_HEX_ENDING;
            }
            files = d.GetFiles(search);
            foreach (FileInfo fi in files)
            {
                foundAChange = ReadCsvFile(fi) || foundAChange;
            }

            return foundAChange;
        }

        protected bool ReadCsvFile(FileInfo fi)
        {
            Console.Out.WriteLine("Reading in CSV data from [" + fi.FullName + "]...");
            int end = fi.Name.LastIndexOf('-');
            int start = fi.Name.IndexOf('-');
            string type = fi.Name.Remove(end).Substring(start + 1);
            string countStr = fi.Name.Remove(start);
            int count = -1;
            bool foundAChange = false;
            if (!Int32.TryParse(countStr, out count))
            {
                Console.Out.WriteLine(String.Format(CSV_OPEN_NAME_ERROR, fi.FullName));
                return false;
            }

            try
            {
                using (var stream = new StreamReader(fi.FullName))
                {
                    int lineNum = 0;
                    string line = stream.ReadLine();
                    if (line != null)
                    {
                        lineNum++; 
                        List<string> headers = GetCsvHeaders(line);

                        while ((line = stream.ReadLine()) != null)
                        {
                            lineNum++;
                            string[] info = line.Split(',');
                            int indexId = -1;
                            if (!Int32.TryParse(info[0], out indexId))
                            {
                                Console.Out.WriteLine(String.Format(CSV_PARSE_ERROR, fi.FullName, lineNum));
                                continue;
                            }
                            if (!_indexes.ContainsKey(indexId))
                            {
                                Console.Out.WriteLine(String.Format(CSV_MATCH_ERROR, fi.FullName, indexId));
                                continue;
                            }
                            List<string> data = info.ToList();
                            data.RemoveAt(0);
                            data.RemoveAt(0);

                            MxeIndexEntry index = _indexes[indexId];
                            if (!index.GetVmTitle().Equals(type))
                            {
                                Console.Out.WriteLine(String.Format(CSV_MATCH_TITLE_ERROR, fi.FullName, lineNum, index.GetVmTitle(), type));
                                continue;
                            }
                            if (!index.GetTypeCode().Equals(count))
                            {
                                Console.Out.WriteLine(String.Format(CSV_MATCH_SIZE_ERROR, fi.FullName, lineNum, index.GetTypeCode(), count));
                                continue;
                            }
                            if (!headers.Count.Equals(data.Count))
                            {
                                Console.Out.WriteLine(String.Format(CSV_MATCH_COUNT_ERROR, fi.FullName, lineNum, headers.Count, data.Count));
                                continue;
                            }
                            foundAChange = index.ReadCsvLineData(headers, data) || foundAChange;
                        }
                    }

                    stream.Close();
                }
            }
            catch (Exception exc)
            {
                Console.Out.WriteLine(exc.ToString());
            }
            
            return foundAChange;
        }

        protected static List<string> GetCsvHeaders(string line)
        {
            string[] temp = line.Split(',');
            List<string> headers = temp.ToList();
            headers.RemoveAt(0);
            headers.RemoveAt(0);

            return headers;
        }
    }
}
