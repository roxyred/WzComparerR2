﻿using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Globalization;

namespace WzComparerR2.WzLib
{
    public class Wz_File
    {
        public Wz_File(string fileName, Wz_Structure wz)
        {
            this.imageCount = 0;
            this.wzStructure = wz;
            this.loaded = InitLoad(fileName);
            this.stringTable = new Dictionary<int, string>();
        }

        private FileStream fileStream;
        private BinaryReader bReader;
        private Wz_Structure wzStructure;
        private Wz_Header header;
        private Wz_Node node;
        private int imageCount;
        private bool loaded;
        private Wz_Type type;

        public readonly object ReadLock = new object();

        internal Dictionary<int, string> stringTable;

        public FileStream FileStream
        {
            get { return fileStream; }
        }

        public BinaryReader BReader
        {
            get { return bReader; }
        }

        public Wz_Structure WzStructure
        {
            get { return wzStructure; }
            set { wzStructure = value; }
        }

        public Wz_Header Header
        {
            get { return header; }
            set { header = value; }
        }

        public Wz_Node Node
        {
            get { return node; }
            set { node = value; }
        }

        public int ImageCount
        {
            get { return imageCount; }
        }

        public bool Loaded
        {
            get { return loaded; }
        }

        public Wz_Type Type
        {
            get { return type; }
            set { type = value; }
        }

        public void Close()
        {
            if (this.bReader != null)
                this.bReader.Close();
            if (this.fileStream != null)
                this.fileStream.Close();
        }

        private bool InitLoad(string fileName)
        {
            this.fileStream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            this.bReader = new BinaryReader(this.FileStream);
            
            return GetHeader(fileName);
        }

        private bool GetHeader(string fileName)
        {
            string signature = new string(this.BReader.ReadChars(4));
            if (signature != "PKG1") { return false; }

            int filesize = (int)this.FileStream.Length;
            int datasize = (int)this.BReader.ReadInt64();
            int headersize = this.BReader.ReadInt32();
            string copyright = new string(this.BReader.ReadChars(headersize - (int)this.FileStream.Position));
            int encver = this.BReader.ReadUInt16();

            this.Header = new Wz_Header(signature, copyright, fileName, headersize, datasize, filesize, encver);
            return true;
        }

        public int ReadInt32()
        {
            int s = this.BReader.ReadSByte();
            return (s == -128) ? this.BReader.ReadInt32() : s;
        }

        public long ReadInt64()
        {
            int s = this.BReader.ReadSByte();
            return (s == -128) ? this.BReader.ReadInt64() : s;
        }

        public float ReadSingle()
        {
            float fl = this.BReader.ReadSByte();
            return (fl == -128) ? this.BReader.ReadSingle() : fl;
        }

        public string ReadString(int offset)
        {
            return ReadString(offset, false);
        }

        public string ReadString(int offset, bool use_enc)
        {
            byte b = this.BReader.ReadByte();
            switch (b)
            {
                case 0x00:
                case 0x73:
                    return ReadString(use_enc);

                case 0x01:
                case 0x1B:
                    return ReadStringAt(offset + this.BReader.ReadInt32(), use_enc);

                case 0x04:
                    this.FileStream.Position += 8;
                    break;

                default:
                    throw new Exception("读取字符串错误 在:" + this.FileStream.Name + " " + this.FileStream.Position);
            }
            return string.Empty;
        }

        public string ReadStringAt(int offset)
        {
            return ReadStringAt(offset, false);
        }

        public string ReadStringAt(int offset, bool use_enc)
        {
            long oldoffset = this.FileStream.Position;
            string str;
            if (!stringTable.TryGetValue(offset, out str))
            {
                this.FileStream.Position = offset;
                str = ReadString(use_enc);
                stringTable[offset] = str;
                this.FileStream.Position = oldoffset;
            }
            return str;
        }

        public string ReadString()
        {
            return ReadString(false);
        }

        public string ReadString(bool use_enc)
        {
            use_enc |= this.WzStructure.encryption.all_strings_encrypted;
            char[] retStr;
            int size = this.BReader.ReadSByte();

            if (size < 0)
            {
                byte mask = 0xAA;
                size = (size == -128) ? this.BReader.ReadInt32() : -size;

                byte[] data = this.BReader.ReadBytes(size);
                retStr = new char[size];
                
                for (int i = 0; i < size; i++)
                {
                    retStr[i] = (char)(data[i] ^ (use_enc ? mask ^ this.WzStructure.encryption.keys[i] : mask));
                    unchecked { mask++; }
                }
                return new string(retStr); 
            }
            else if (size > 0)
            {
                int mask = 0xAAAA;
                if (size == 127)
                {
                    size = this.BReader.ReadInt32();
                }

                byte[] data = this.BReader.ReadBytes(size * 2);
                retStr = new char[size];
                for (int i = 0; i < size; i++)
                {
                    retStr[i] = ((char)(data[2 * i] + data[2 * i + 1] * 0x100 ^
                        (use_enc ? mask ^ this.WzStructure.encryption.keys[2 * i] ^ (this.WzStructure.encryption.keys[2 * i + 1] * 0x100) : mask)));
                    unchecked { mask++; }
                }
                return new string(retStr); 
            }
            else
            {
                return string.Empty;
            }
        }

        public int CalcOffset()
        {
            uint offset = (uint)(this.FileStream.Position - 0x3C) ^ 0xFFFFFFFF;
            uint bytes = this.BReader.ReadUInt32();
            int distance = 0;
            long pos = this.FileStream.Position;

            offset *= this.Header.HashVersion;
            offset -= 0x581C3F6D;
            distance = (int)offset & 0x1F;
            offset = (offset << distance) | (offset >> (32 - distance));
            offset ^= bytes;
            offset += 0x78;

            return (int)offset;
        }

        public void GetDirTree(Wz_Node parent, bool allChildOnList)
        {
            GetDirTree(parent, allChildOnList, true);
        }

        public void GetDirTree(Wz_Node parent, bool allChildOnList, bool useBaseWz)
        {
            List<string> dirs = new List<string>();
            string name = null;
            int size = 0;
            int cs32 = 0;
            int offs = 0;
            bool on_list = false;
            bool all_lst = allChildOnList || this.WzStructure.encryption.List.Contains(parent.Text.ToLower() + '/');
            bool parentBase = parent.Text.Equals("base.wz", StringComparison.CurrentCultureIgnoreCase);

            int count = ReadInt32();

            for (int i = 0; i < count; i++)
            {
                switch ((int)this.BReader.ReadByte())
                {
                    case 0x02:
                        name = this.ReadStringAt(this.Header.HeaderSize + 1 + this.BReader.ReadInt32());
                        goto case 0xffff;
                    case 0x04:
                        name = this.ReadString();
                        goto case 0xffff;

                    case 0xffff:
                        size = this.ReadInt32();
                        cs32 = this.ReadInt32();
                        on_list = all_lst
                             || (parentBase ? this.WzStructure.encryption.list_contains(name.ToLower()) :
                            this.WzStructure.encryption.list_contains(getFullPath(parent, name)));

                        Wz_Image img = null;
                        if (!this.header.VersionChecked) //用第一个img测试版本
                        {
                            long pos = this.fileStream.Position;
                            while (this.header.TryGetNextVersion())
                            {
                                this.fileStream.Position = pos;
                                offs = CalcOffset();
                                if (offs < this.header.HeaderSize || offs + size > this.fileStream.Length)  //img块越界
                                {
                                    continue;
                                }
                                this.fileStream.Position = offs;
                                switch (this.fileStream.ReadByte())
                                {
                                    case 0x73:
                                    case 0x1b:
                                        //试读img第一个string
                                        break;
                                    default:
                                        continue;
                                }

                                img = new Wz_Image(name, size, cs32, offs, on_list, this);

                                if (img.TryExtract()) //试读成功
                                {
                                    img.Unextract();
                                    this.header.VersionChecked = true;
                                    break;
                                }
                            }
                            this.fileStream.Position = pos + 4;

                            if (!this.header.VersionChecked) //最终测试失败 那就失败吧..
                            {
                                this.header.VersionChecked = true;
                            }
                        }
                        else
                        {
                            offs = CalcOffset();
                            img = new Wz_Image(name, size, cs32, offs, on_list, this);
                        }
                        Wz_Node childNode = parent.Nodes.Add(name);
                        childNode.Value = img;
                        img.OwnerNode = childNode;

                        this.imageCount++;
                        break;

                    case 0x03:
                        name = this.ReadString();
                        size = this.ReadInt32();
                        cs32 = this.ReadInt32();
                        this.FileStream.Position += 4;
                        dirs.Add(name);
                        break;
                }
            }

            foreach (string dir in dirs)
            {
                Wz_Node t = parent.Nodes.Add(dir);
                if (parentBase && useBaseWz)
                {
                    this.WzStructure.has_basewz = true;
                    GetDirTree(t, false);
                    try
                    {
                        string filePath = Path.Combine(Path.GetDirectoryName(this.Header.FileName), dir + ".wz");
                        if (File.Exists(filePath))
                            this.WzStructure.LoadFile(filePath, t);
                    }
                    catch
                    {
                    }
                }
                else
                {
                    GetDirTree(t, all_lst);
                }
            }
        }

       private string getFullPath(Wz_Node parent, string name)
        {
            List<string> path = new List<string>(5);
            path.Add(name.ToLower());
            while (parent != null && !(parent.Value is Wz_File))
            {
                path.Insert(0, parent.Text.ToLower());
                parent = parent.ParentNode;
            }
            if (parent != null)
            {
                path.Insert(0, parent.Text.ToLower().Replace(".wz", ""));
            }
            return string.Join("/", path.ToArray());
        }

       public void DetectWzType()
       {
           this.type = Wz_Type.Unknown;
           if (this.node == null)
           {
               return;
           }
           if (this.node.Nodes.Count < 100)
           {
               foreach (Wz_Node subNode in this.node.Nodes)
               {
                   switch (subNode.Text)
                   {
                       case "smap.img":
                       case "zmap.img":
                           this.type = Wz_Type.Base;
                           break;

                       case "00002000.img":
                       case "Accessory":
                       case "Weapon":
                           this.type = Wz_Type.Character;
                           break;

                       case "BasicEff.img":
                       case "SetItemInfoEff.img":
                           this.type = Wz_Type.Effect;
                           break;

                       case "Commodity.img":
                       case "Curse.img":
                           this.type = Wz_Type.Etc;
                           break;

                       case "Cash":
                       case "Consume":
                           this.type = Wz_Type.Item;
                           break;

                       case "Back":
                       case "Physics.img":
                           this.type = Wz_Type.Map;
                           break;

                       case "PQuest.img":
                       case "QuestData":
                           this.type = Wz_Type.Quest;
                           break;

                       case "Attacktype.img":
                       case "MobSkill.img":
                           this.type = Wz_Type.Skill;
                           break;

                       case "Bgm00.img":
                       case "BgmUI.img":
                           this.type = Wz_Type.Sound;
                           break;

                       case "MonsterBook.img":
                       case "EULA.img":
                           this.type = Wz_Type.String;
                           break;

                       case "CashShop.img":
                       case "UIWindow.img":
                           this.type = Wz_Type.UI;
                           break;

                   }
                   if (this.type != Wz_Type.Unknown)
                       return;
               }
           }


           if (this.type == Wz_Type.Unknown)
           {
               string wzName = this.node.Text;
               int idx = wzName.IndexOf(".wz", StringComparison.CurrentCultureIgnoreCase);
               if (idx >= 0)
                   wzName = wzName.Substring(0, idx);
               try
               {
                   this.type = (Wz_Type)Enum.Parse(typeof(Wz_Type), wzName, true);
               }
               catch
               {
                   this.type = Wz_Type.Unknown;
               }
           }
       }
    }
}
