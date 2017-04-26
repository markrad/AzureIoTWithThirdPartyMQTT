﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace AzureIotWithThirdPartyMQTT
{
    /// <summary>
    /// Demonstration of connecting to an Azure IoT hub using native MQTT rather than the C# class in the SDK
    /// See https://azure.microsoft.com/en-us/documentation/articles/iot-hub-mqtt-support/#using-the-mqtt-protocol-directly
    /// </summary>
    class Program
    {
        private static readonly DateTime EPOCH = new DateTime(1970, 1, 1, 0, 0, 0);
        private static AutoResetEvent waitForUnsubscribe = new AutoResetEvent(false);

        /// <summary>
        /// Parses a connection string a makes its contents available via properties
        /// </summary>
        private class ConnectionInfo
        {
            /// <summary>
            /// Extract values from connection string
            /// </summary>
            /// <param name="connectionString">Connection string in format generated by the Device Explorer utility</param>
            /// <param name="expiresInMinutes">Number of minutes before SAS token expiration</param>
            public ConnectionInfo(string connectionString, int expiresInMinutes = 60)
            {
                const string SHARED_ACCESS_KEY_NAME = "sharedaccesskey=";

                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new ArgumentNullException("connectionString");

                string[] values = connectionString.Split(';');

                if (values.Length != 3)
                    throw new ArgumentException("Invalid connection string", "connectionString");

                if (values[0].ToLower().StartsWith("hostname=") == false ||
                    values[1].ToLower().StartsWith("deviceid=") == false ||
                    values[2].ToLower().StartsWith(SHARED_ACCESS_KEY_NAME) == false)
                    throw new ArgumentException("Invalid connection string", "connectionString");

                HostName = values[0].Split('=')[1];
                DeviceId = values[1].Split('=')[1];
                SharedAccessKey = values[2].Substring(SHARED_ACCESS_KEY_NAME.Length);

                if (string.IsNullOrWhiteSpace(HostName) ||
                    string.IsNullOrWhiteSpace(DeviceId) ||
                    string.IsNullOrWhiteSpace(SharedAccessKey))
                    throw new ArgumentException("Invalid connection string", "connectionString");

                Password = GeneratePassword(HostName + "/devices/" + DeviceId, SharedAccessKey, expiresInMinutes);
            }

            private static string GeneratePassword(string resourceUri, string signingKey, int expiresInMinutes)
            {
                resourceUri = WebUtility.UrlEncode(resourceUri);

                DateTime expiresOn = DateTime.UtcNow.AddMinutes(expiresInMinutes);
                TimeSpan secondsFromBaseTime = expiresOn.Subtract(EPOCH);
                string toSign = string.Format("{0}\n{1}", resourceUri, Math.Ceiling(secondsFromBaseTime.TotalSeconds));
                string signed = "";
                string result = "";

#if DEBUG
                byte[] test = Convert.FromBase64String(signingKey);
                Debug.WriteLine(BitConverter.ToString(test).Replace("-", string.Empty));
                Debug.WriteLine(System.Text.Encoding.UTF8.GetString(test, 0, test.Length));
#endif

                using (HMACSHA256 hmac = new HMACSHA256(Convert.FromBase64String(signingKey)))
                {
                    Debug.WriteLine(BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(toSign))));

                    signed = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(toSign)));
                }

                signed = WebUtility.UrlEncode(signed);
                result = string.Format("SharedAccessSignature sr={0}&sig={1}&se={2}", resourceUri, signed, Math.Ceiling(secondsFromBaseTime.TotalSeconds));

                return result;
            }

            public string HostName { get; private set; }
            public string DeviceId { get; private set; }
            public string SharedAccessKey { get; private set; }
            public string Password { get; private set; }
        }

        /// <summary>
        /// Main entry point
        /// </summary>
        /// <param name="args">One argument containing a connection string in the format generated by the Device Explorer utility</param>
        /// <returns></returns>
        static int Main(string[] args)
        {
            if (args.Length != 1)
                throw new ArgumentException("Invalid number of command line arguments - provide a connection string");

            // Parse the connection string
            ConnectionInfo connectionInfo = new ConnectionInfo(args[0]);

            // Construct the values required to publish and subscribe on the Azure IoT hub via MQTT
            string userName = connectionInfo.HostName + "/" + connectionInfo.DeviceId;                     
            string publishTopic = "devices/" + connectionInfo.DeviceId + "/messages/events/";
            string subscribeTopic = "devices/" + connectionInfo.DeviceId + "/messages/devicebound/#";
            const int port = 8883;
            const int messageCount = 2;

            MqttClient publisher;

            // Construct the MQTT client and set up the call backs
            publisher = new MqttClient(connectionInfo.HostName, port, true, MqttSslProtocols.TLSv1_0, null, null);
            publisher.MqttMsgPublished += Publisher_MqttMsgPublished;
            publisher.Subscribe(new string[] { subscribeTopic }, new byte[] { MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE });
            publisher.MqttMsgPublishReceived += Publisher_MqttMsgPublishReceived;
            publisher.ConnectionClosed += Publisher_ConnectionClosed;
            publisher.MqttMsgSubscribed += Publisher_MqttMsgSubscribed;
            publisher.MqttMsgUnsubscribed += Publisher_MqttMsgUnsubscribed;

            Console.WriteLine("Connecting to " + connectionInfo.DeviceId);

            byte code;

            // Connect to the Azure IoT hub
            //code = publisher.Connect(connectionInfo.DeviceId, userName, connectionInfo.Password);
            code = publisher.Connect(connectionInfo.DeviceId, userName, connectionInfo.Password, false, 60);

            if (code != 0)
            {
                Console.WriteLine("Failed to connect publisher {0}", code);
                return -1;
            }

            Console.WriteLine("CleanSession=" + publisher.CleanSession);

            // Send some messages
            for (int i = 0; i < messageCount; i++)
            {
                ushort msgId = publisher.Publish(publishTopic, Encoding.UTF8.GetBytes("Test Message: " + i), 1, false);
                Console.WriteLine("Sent message {0}", msgId);
                Thread.Sleep(2000);
            }

            // Wait for input before exiting
            Console.ReadLine();

            // Clean up and exit
            if (publisher.IsConnected)
            {
                //publisher.Unsubscribe(new string[] { subscribeTopic });
                //waitForUnsubscribe.WaitOne(20000);
                publisher.Disconnect();
            }
            
            return 0;
        }

        private static void Publisher_MqttMsgUnsubscribed(object sender, MqttMsgUnsubscribedEventArgs e)
        {
            Console.WriteLine("Unsubscribed");
            waitForUnsubscribe.Set();
        }

        private static void Publisher_MqttMsgSubscribed(object sender, MqttMsgSubscribedEventArgs e)
        {
            Console.WriteLine("Subscribed");
        }

        private static void Publisher_ConnectionClosed(object sender, EventArgs e)
        {
            Console.WriteLine("Connection closed");
        }

        private static void Publisher_MqttMsgPublished(object sender, uPLibrary.Networking.M2Mqtt.Messages.MqttMsgPublishedEventArgs e)
        {
            Console.WriteLine("Message {0} Published {1}", e.MessageId, e.IsPublished);
        }

        private static void Publisher_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
            Console.WriteLine("Message received: " + Encoding.UTF8.GetString(e.Message) + "\n\ton topic: " + e.Topic);
        }
    }
}
