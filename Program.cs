﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Configuration;
using System.Data;
using System.IO;
using System.Security.Cryptography;
using Server_Proxy;
using Server_Proxy.localhost;
using System.Web.Services.Protocols;
using CommandLine;
using System.Data.SqlClient;
using System.Net;
using System.Security.Authentication;
using log4net;
using log4net.Config;

namespace AccountingFiles
{
    public class Options
    {
        [Option('e', "env", Required = true, HelpText = "nonPROD or PROD")]
        public string Env { get; set; }

        [Option('d', "debug", Required = false, HelpText = "Show detailed information")]
        public bool Debug { get; set; }
    }

    class Program
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(Program));
        private const string AesIV256 = @"!QAZ2WSX#EDC4RFV";
        private const string AesKey256 = @"5TGB&YHN7UJM(IK<5TGB&YHN7UJM(IK<";

        static void Main(string[] args)
        {
            try
            {
                XmlConfigurator.Configure();
                log.Info("START ------------------------------------------------------");

                var result = CommandLine.Parser.Default.ParseArguments<Options>(args);

                result.WithParsed(opts =>
                {
                    Run(opts);
                });

                result.WithNotParsed(errs =>
                {
                    Environment.Exit(1);
                });
                log.Info("END ------------------------------------------------------");

            }
            catch (Exception ex)
            {
                log.Error(ex.ToString());
                Console.WriteLine(ex.ToString());
            }
        }


        static void Run(Options options)
        {
            try
            {
                Program ot = new Program();
                string path = ot.GetPath();
                log.Info("Running in environment: " + options.Env);
                log.Info("Working directory: " + path);

                // Read porgrpik from app.config
                string porgrpik_string = ConfigurationManager.AppSettings["porgrpik"];

                string[] ik = porgrpik_string.Split(',');
                List<string> list_porgrpik = new List<string>();
                foreach (string iks in ik)
                {
                    list_porgrpik.Add(iks);
                }

                //Open MSSQL DB
                SqlConnection DBReturn = ot.MSSQLopenDB(options);

                //Get password from Enigma
                log.Info("Get password...");
                DataTable ret = ot.GetPassword(ConfigurationManager.AppSettings[options.Env + "_iHubId"], DBReturn);
                if (options.Debug)
                {
                    log.Info("iHub User::: " + ret.Rows[0][0].ToString());
                }

                //Close MSSQL DB
                log.Info("Close database '" + options.Env + "_Enigma'...");
                ot.MSSQLcloseDB(DBReturn);

                //open db
                Oracle.DataAccess.Client.OracleConnection con = ot.OraConnect(options);

                // Logg på iHub server via Web Service
                string iHubURL = ConfigurationManager.AppSettings[options.Env + "_iHubURL"];
                log.Info("Connect to iHub webservice: " + iHubURL);
                string iHubBrukernavn = ConfigurationManager.AppSettings["iHubBrukernavn"];
                string iHubVolum = ConfigurationManager.AppSettings["iHubVolum"];
                const SslProtocols _Tls12 = (SslProtocols)0x00000C00;
                const SecurityProtocolType Tls12 = (SecurityProtocolType)_Tls12;
                ServicePointManager.SecurityProtocol = Tls12;
                Login(out ActuateAPI l_proxy, iHubURL, iHubBrukernavn, ret.Rows[0][1].ToString(), iHubVolum, options);


                //Navigate to iHub folder
                string iHubFolder = ConfigurationManager.AppSettings["iHubFolder"];
                string iHubkunde = ConfigurationManager.AppSettings["iHubkunde"];
                DateTime t = DateTime.Now.AddMonths(-1);
                string format_year = "yyyy";
                string format_month = "MM";
                string iHubAar = t.ToString(format_year);
                string iHubMaaned = t.ToString(format_month);
                string iHubFolderkunde = iHubFolder + iHubkunde + iHubAar + "/" + iHubMaaned + "/";
                log.Info("NHO iHub folder: " + iHubFolderkunde);

                for (int i = 0; i < list_porgrpik.Count; i++)
                {
                    string porgrpik = list_porgrpik[i];
                    string sql = ot.SQLString(porgrpik);

                    // Get data from Database, put into DataTable
                    DataTable dt = ot.GetDataFromOraDB(sql, con);
                    log.Info("Get data for porgrpik from DB: " + porgrpik);

                    // Write file
                    ot.WriteFile(ot.GetFileName(porgrpik), dt);
                    log.Info("Write file: " + path + @"\" + ot.GetFileName(porgrpik));

                    // Send file to iHub Webservice
                    ot.KalliHubAPI(iHubFolderkunde, iHubVolum, l_proxy, ot.GetFileName(porgrpik), path);

                    //Delete file 
                    log.Info("Delete file: " + path + @"\" + ot.GetFileName(porgrpik));
                    System.IO.File.Delete(path + "\\" + ot.GetFileName(porgrpik));
                }

                // Close DB
                ot.OraClose(con);
                log.Info("Close database '" + options.Env + "_SCD'...");

                // Les nye kunder Ferd, Viken Pensjonskasse, Fellesordningen for AFP SLV
                string iHubNyekunder = ConfigurationManager.AppSettings["iHubNyekunder"];
                string[] ank = iHubNyekunder.Split(',');

                foreach (string ank_num in ank)
                {
                    log.Info("Kunde: " + ank_num);
                    iHubFolderkunde = iHubFolder + ank_num + "/" + iHubAar + "/" + iHubMaaned + "/";
                    log.Info("iHubFolderkunde: " + iHubFolderkunde);
                    string[] fileEntries = Directory.GetFiles(ConfigurationManager.AppSettings[options.Env + "_FileFolder"], "*" + ConfigurationManager.AppSettings["FileNames" + ank_num] + "*.*");
                    if (fileEntries.Length != 0)
                    {
                        foreach (string fileName in fileEntries)
                        {
                            log.Info("Filename: " + fileName);
                            ot.KalliHubAPI(iHubFolderkunde, iHubVolum, l_proxy, Path.GetFileName(fileName), ConfigurationManager.AppSettings[options.Env + "_FileFolder"]);

                            //moving file to archive
                            log.Info("move " + fileName + " to " + ConfigurationManager.AppSettings[options.Env + "_FileFolder"] + "\\archive\\" + Path.GetFileName(fileName));
                            System.IO.File.Move(fileName, ConfigurationManager.AppSettings[options.Env + "_FileFolder"] + "\\archive\\" + Path.GetFileName(fileName));
                        }
                    } else
                    {
                        log.Info("No files for upload...");
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        /// <summary>
        /// SOAP parser for å få ut feilmeldiinger
        /// </summary>	
        static void PrintExceptionDetails(Exception e)
        {
            try
            {
                if (e is SoapException l_e)
                {
                    Console.WriteLine("# SOAP Fault received: ");
                    for (int i = 0; i < l_e.Detail.ChildNodes.Count; i++)
                    {
                        if (l_e.Detail.ChildNodes.Item(i).Name[0] != '#')
                        {
                            if (l_e.Detail.ChildNodes.Item(i).Name == "Description")
                            {
                                for (int j = 0; j < l_e.Detail.ChildNodes.Item(i).ChildNodes.Count; j++)
                                {
                                    if (l_e.Detail.ChildNodes.Item(i).ChildNodes.Item(j).Name[0] != '#')
                                    {
                                        Console.WriteLine(l_e.Detail.ChildNodes.Item(i).ChildNodes.Item(j).Name + ": "
                                            + l_e.Detail.ChildNodes.Item(i).ChildNodes.Item(j).InnerText);
                                    }
                                }
                            }
                            else
                            {
                                Console.WriteLine(l_e.Detail.ChildNodes.Item(i).Name + ": " + l_e.Detail.ChildNodes.Item(i).InnerText);
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine("# Non-SOAP exception:");
                    Console.WriteLine(e.ToString());
                }
            }
            catch (Exception ex)
            {
                log.Error(ex.Message.ToString());
                Console.WriteLine(ex.Message.ToString());
            }
        }


        /// <summary>
        /// Logger på iHub server
        /// </summary>	
        static void Login(out ActuateAPI proxy, string url, string userName, string password, string volume, Options options)
        {
            // Password is stored in Enigma as ' ' It is not possible to store an empty password. The password to iHub in nonPROD is an empty password. Need to set that.
            if (options.Env.Equals("nonPROD", StringComparison.InvariantCultureIgnoreCase)) {
                password = "";
            }

            //	Create an instance of server proxy. Construct an empty header.
            proxy = new ActuateAPI
            {
                Url = url,
                HeaderValue = new Header()
            };
            proxy.HeaderValue.TargetVolume = volume;

            //	Prepare Login reqest parameters
            Login l_LoginReq = new Login
            {
                User = userName,
                Password = password
            };

            log.Info("Login to iHub with user: " + userName);

            //	Send Login reqest, handle SOAP exception if any
            LoginResponse l_loginRes;
            try
            {
                l_loginRes = proxy.login(l_LoginReq);
            }
            catch (Exception e)
            {
                PrintExceptionDetails(e);
                return;
            }

            //	Store authentication id
            proxy.HeaderValue.AuthId = l_loginRes.AuthId;
        }


        private void KalliHubAPI(string iHubFolderkunde, string iHubVolum, ActuateAPI l_proxy, string filename, string path)
        {
            try
            {

                // Create extended proxy inctance
                ActuateAPIEx l_proxyEx = new ActuateAPIEx
                {
                    Url = l_proxy.Url,
                    HeaderValue = new Header
                    {
                        AuthId = l_proxy.HeaderValue.AuthId,
                        TargetVolume = iHubVolum
                    }
                };

                // **** Set permission object
                // ****
                // Read Roles from app.config
                string roles_string = ConfigurationManager.AppSettings["Roles"];
                string rights = ConfigurationManager.AppSettings["Roles_Rights"];
                string rights_extended = ConfigurationManager.AppSettings["Roles_Rights_Extended"];

                string[] Roles = roles_string.Split(',');
                List<string> list_roles = new List<string>();
                int counter = 0;
                int num = 0;

                foreach (string r in Roles)
                {
                    list_roles.Add(r);
                    num += 1;
                }
                Permission[] paPermission = new Permission[num];

                foreach (var role in list_roles)
                {
                    Permission pPermission = new Permission
                    {
                        ItemElementName = ItemChoiceType1.RoleName,

                        Item = role
                    };
                    if (role == "Client Reporting")
                    {
                        pPermission.AccessRight = rights_extended;
                    }
                    else
                    {
                        pPermission.AccessRight = rights;
                    }
                    paPermission[counter] = pPermission;
                    counter += 1;
                }

                PermissionArray paPermissionArray = new PermissionArray
                {
                    Permission = paPermission
                };

                UpdateFileOperation l_UpdateFileOperation = new UpdateFileOperation();
                UpdateFileOperation[] ufoaUpdateFileOperation = new UpdateFileOperation[1];

                l_UpdateFileOperation.ItemElementName = ItemChoiceType26.SetPermissions;
                l_UpdateFileOperation.Item = paPermissionArray;
                ufoaUpdateFileOperation[0] = l_UpdateFileOperation;

                UpdateFile l_UpdateFile1 = new UpdateFile
                {
                    Item1ElementName = Item1ChoiceType14.Name,
                    Item1 = iHubFolderkunde,
                    UpdateFileOperationGroup = ufoaUpdateFileOperation
                };

                //// Set Administration object
                AdminOperation aoAdminOperation = new AdminOperation();
                AdminOperation[] aoaAdminOperation = new AdminOperation[1];
                aoAdminOperation.Item = l_UpdateFile1;
                aoaAdminOperation[0] = aoAdminOperation;

                // Prepare UploadFile request
                UploadFile l_UploadFile = new UploadFile
                {

                    // Set NewFile object
                    NewFile = new NewFile()
                };
                l_UploadFile.NewFile.Name = iHubFolderkunde + filename;
                l_UploadFile.NewFile.ReplaceExisting = true;
                l_UploadFile.NewFile.ReplaceExistingSpecified = true;
                l_UploadFile.NewFile.MaxVersions = 1;
                l_UploadFile.NewFile.MaxVersionsSpecified = true;
                l_UploadFile.Content = new Attachment
                {
                    ContentType = "binary",
                    ContentId = "Attachment"
                };
                l_UploadFile.NewFile.ACL = paPermission;

                //Open the file to be uploaded
                ActuateAPIEx.UploadStream = new FileStream(path + "\\" + filename, FileMode.Open);
                log.Info("Upload file to iHub: " + iHubFolderkunde + filename);

                // Send request
                UploadFileResponse l_res;
                AdministrateResponse admresponse;
                l_res = l_proxyEx.uploadFile(l_UploadFile);
                admresponse = l_proxy.administrate(aoaAdminOperation);

                ActuateAPIEx.UploadStream.Close();
                l_proxyEx.Dispose();
            }
            catch (Exception e)
            {
                log.Error("Sjekk at kunden eksisterer og har nødvendige roller");
                Console.WriteLine("Sjekk at kunden eksisterer og har nødvendige roller");
                log.Error(e.ToString());
                Console.WriteLine(e.ToString());
                //PrintExceptionDetails(e);
            }
        }

        /// <summary>
        /// Dekrypterer en streng
        /// </summary>
        /// <param name="encrypted">Den krypterte strengen</param>
        /// <param name="key">Nøkkelen som skal brukes til dekryptering</param>
        /// <returns>Den dekrypterte strengen</returns>
        public string Decrypt(string encrypted, string key)
        {
            try
            {
                TripleDESCryptoServiceProvider des;
                MD5CryptoServiceProvider hashmd5;
                byte[] keyhash, buff;
                string decrypted;
                hashmd5 = new MD5CryptoServiceProvider();
                keyhash = hashmd5.ComputeHash(ASCIIEncoding.ASCII.GetBytes(key));
                hashmd5 = null;
                des = new TripleDESCryptoServiceProvider
                {
                    Key = keyhash,
                    Mode = CipherMode.ECB
                };
                buff = Convert.FromBase64String(encrypted);
                decrypted = ASCIIEncoding.ASCII.GetString(
                des.CreateDecryptor().TransformFinalBlock(buff, 0, buff.Length));

                return decrypted;
            }
            catch (Exception ex)
            {
                log.Error("***FATAL Feil***: Fikk ikke dekryptert nøkkel: \n" + "\n" + ex.Message.ToString());
                throw new Exception("***FATAL Feil***: Fikk ikke dekryptert nøkkel: \n" + "\n" + ex.Message.ToString());
            }

        }


        //Ora connect
        public Oracle.DataAccess.Client.OracleConnection OraConnect(Options options)
        {

            log.Info("Open database '" + options.Env + "_SCD'...");
            Oracle.DataAccess.Client.OracleConnection con = new Oracle.DataAccess.Client.OracleConnection
            {
                ConnectionString = String_Decrypt(ConfigurationManager.ConnectionStrings[options.Env + "_SCD"].ConnectionString)
            };
            if (options.Debug)
            {
                string[] debug_info_split = con.ConnectionString.Split(new string[] { "Password=" }, StringSplitOptions.None);
                log.Info("Connection string::: " + debug_info_split[0].ToString());
            }
            try
            {
                Oracle.DataAccess.Client.OracleCommand command = con.CreateCommand();
                con.Open();
                return con;
            }
            catch (Exception ex)
            {
                log.Error(ex.ToString());
                Console.WriteLine(ex.ToString());
                Environment.Exit(1);
                return null;
            }
        }

        //Get path
        public string GetPath()
        {
            try
            {
                string path = Environment.CurrentDirectory;
                return path;
            }
            catch (Exception ex)
            {
                log.Error(ex.ToString());
                Console.WriteLine(ex.ToString());
                return null;
            }
        }

        //Get filename
        public string GetFileName(string porgrpik)
        {
            try
            {
                string porgrp = ConfigurationManager.AppSettings[porgrpik];
                DateTime t = DateTime.Now.AddMonths(-1);
                string format_year = "yyyy";
                string format_month = "MMMM";
                string filename = "nho_regnskap_" + t.ToString(format_month) + "_" + t.ToString(format_year) + "_" + porgrp + ".txt";

                return filename;
            }
            catch (Exception ex)
            {
                log.Error(ex.ToString());
                Console.WriteLine(ex.ToString());
                return null;
            }
        }


        //Ora close
        public void OraClose(Oracle.DataAccess.Client.OracleConnection con)
        {
            try
            {
                con.Close();
                con.Dispose();
            }
            catch (Exception ex)
            {
                log.Error(ex.ToString());
                Console.WriteLine(ex.ToString());
                Environment.Exit(1);
            }
        }

        // Les fra Database og returner en DataTable med innholdet
        public DataTable GetDataFromOraDB(string query, Oracle.DataAccess.Client.OracleConnection con)
        {
            Oracle.DataAccess.Client.OracleCommand command = con.CreateCommand();
            command.CommandText = query;
            var dt = new DataTable();
            try
            {
                dt.Load(command.ExecuteReader());
            }
            catch (Exception ex)
            {
                log.Error(ex.ToString());
                Console.WriteLine(ex.ToString());
                Environment.Exit(1);
            }
            return dt;
        }

        public void WriteFile(string filename, DataTable dt)
        {
            string path = GetPath();
            filename = path + "\\" + filename;

            try
            {
                var result = new StringBuilder();
                foreach (DataRow row in dt.Rows)
                {
                    for (int i = 0; i < dt.Columns.Count; i++)
                    {
                        string temp = row[i].ToString();
                        if (temp.Contains(","))
                        {
                            temp = temp.Replace(",", ".");
                            result.Append(temp);
                        }
                        else
                        {
                            result.Append(row[i].ToString());
                        }
                        result.Append(i == dt.Columns.Count - 1 ? "" : ",");
                        //result.Append(i == dt.Columns.Count - 1 ? "\n" : ",");
                    }
                    result.AppendLine();
                }

                StreamWriter objWriter = new StreamWriter(filename, false);
                objWriter.WriteLine(result.ToString());
                objWriter.Close();
            }
            catch (Exception ex)
            {
                log.Error(ex.ToString());
                Console.WriteLine(ex.ToString());
                Environment.Exit(1);
            }
        }

        public string SQLString(string por)
        {
            try
            {
                // Read SQL string from app.config
                string sql = ConfigurationManager.AppSettings["sqlstring"];

                string streng = "\"";
                string streng2 = "||";
                string streng3 = "\"\"";
                string streng4 = "<>";
                sql = sql.Replace("yyyyyy", streng);
                sql = sql.Replace("xxxxxx", streng2);
                sql = sql.Replace("zzzzzz", streng3);
                sql = sql.Replace("ffffff", streng4);
                sql = sql.Replace("kkkkkk", por);

                return sql;
            }
            catch (Exception e)
            {
                log.Error(e.ToString());
                Console.WriteLine(e.ToString());
                return null;
            }
        }

        // MSSQL Close 
        public void MSSQLcloseDB(SqlConnection thisConnection)
        {
            try
            {
                thisConnection.Close();
            }
            catch (Exception e)
            {
                log.Error(e.ToString());
                Console.WriteLine(e.ToString());
            }
        }

        // MSSQL Open
        public SqlConnection MSSQLopenDB(Options options)
        {
            try
            {
                log.Info("Open database '" + options.Env + "_Enigma'...");
                SqlConnection conn = null;
                if (options.Debug)
                {
                    string debug_info = String_Decrypt(ConfigurationManager.ConnectionStrings[options.Env + "_Enigma"].ConnectionString);
                    string[] debug_info_split = debug_info.Split(new string[] { "password=" }, StringSplitOptions.None);
                    log.Info("Connection string::: " + debug_info_split[0].ToString());
                } 
                conn = new SqlConnection(String_Decrypt(ConfigurationManager.ConnectionStrings[options.Env + "_Enigma"].ConnectionString));
                conn.Open();

                return conn;
            }
            catch (Exception e)
            {
                log.Error(e.ToString());
                Console.WriteLine(e.ToString());
                return null;
            }
        }

        public string String_Decrypt(string streng)
        {
            try
            {
                string[] connection_string = streng.Split(';');
                StringBuilder builder = new StringBuilder();

                foreach (string con in connection_string)
                {
                    if (con.ToLower().Contains("password") || con.ToLower().Contains("id"))
                    {
                        string[] items = con.Split(new char[] { '=' }, 2);
                        string firstItem = items[0];
                        string secondItem = items[1];

                        secondItem = Decrypt256(secondItem);
                        builder.Append(";").Append(firstItem).Append("=").Append(secondItem);
                    }
                    else
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append(";").Append(con);
                        }
                        else
                        {
                            builder.Append(con);
                        }
                    }
                }
                return builder.ToString();
            }
            catch (Exception e)
            {
                log.Error(e.ToString());
                Console.WriteLine(e.ToString());
                return null;
            }
        }

        private string Decrypt256(string text)
        {
            try
            {
                AesCryptoServiceProvider aes = new AesCryptoServiceProvider
                {
                    BlockSize = 128,
                    KeySize = 256,
                    IV = Encoding.UTF8.GetBytes(AesIV256),
                    Key = Encoding.UTF8.GetBytes(AesKey256),
                    Mode = CipherMode.CBC,
                    Padding = PaddingMode.PKCS7
                };

                // Convert Base64 strings to byte array
                byte[] src = System.Convert.FromBase64String(text);

                // decryption
                using (ICryptoTransform decrypt = aes.CreateDecryptor())
                {
                    byte[] dest = decrypt.TransformFinalBlock(src, 0, src.Length);
                    return Encoding.Unicode.GetString(dest);
                }
            }
            catch (Exception e)
            {
                log.Error(e.ToString());
                Console.WriteLine(e.ToString());
                return null;
            }
        }


        public DataTable GetPassword(string id, SqlConnection DBReturn)
        {
            try
            {
                DataTable dt;
                string SQL = @"OPEN SYMMETRIC KEY SSN_Key_01
                            DECRYPTION BY CERTIFICATE Enigma2015;
                            select  Username,
                                    CONVERT(nvarchar, DecryptByKey(Password)) AS 'Passord'
                            from EnigmaPassordv2
                            where id = @param1";

                using (SqlCommand cmd = new SqlCommand(SQL, DBReturn))
                {
                    cmd.Parameters.AddWithValue("@param1", id);
                    cmd.CommandText = SQL;
                    dt = GetDataFromMSSQLDBv2(cmd);
                }
                return dt;
            }
            catch (Exception e)
            {
                log.Error(e.ToString());
                Console.WriteLine(e.ToString());
                return null;
            }
        }

        //Les fra Database og returner en DataTable med innholdet
        public DataTable GetDataFromMSSQLDBv2(SqlCommand cmd)
        {
            try
            {
                cmd.CommandTimeout = 0;
                DataTable t1 = new DataTable();
                SqlDataAdapter a = new SqlDataAdapter(cmd);
                a.Fill(t1);

                return t1;
            }
            catch (Exception e)
            {
                log.Error(e.ToString());
                Console.WriteLine(e);
                return null;
            }
        }
    }
}
