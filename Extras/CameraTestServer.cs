using UnityEngine;
using System;
using System.Collections; 
using System.Collections.Generic;
using System.Net; 
using System.Net.Sockets; 
using System.Text; 
using System.Threading;
// using System.Diagnostics;
using System.Runtime.InteropServices;

//
// This is a very basic prototype for a "moving camera server" that can
// receive the camera pose updates from Reality Mixer.
//
// Instructions:
//
// 1. Add an empty Game Object to your scene and add this script to that Game Object.
//

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CameraPayload {
    public uint protocolIdentifier;
    public float px;
    public float py;
    public float pz;
    public float qx;
    public float qy;
    public float qz;
    public float qw;

    public const int StructSize = sizeof(uint) + 7 * sizeof(float);
    public const uint identifier = 13371337;

    public static CameraPayload FromBytes(byte[] arr) {
        CameraPayload payload = new CameraPayload();

        int size = Marshal.SizeOf(payload);
        // Trace.Assert(size == StructSize);

        IntPtr ptr = Marshal.AllocHGlobal(size);

        Marshal.Copy(arr, 0, ptr, size);

        payload = (CameraPayload)Marshal.PtrToStructure(ptr, payload.GetType());
        Marshal.FreeHGlobal(ptr);

        return payload;
    }

    public OVRPose ToPose() {
        OVRPose result = new OVRPose();
        result.position = new Vector3(px, py, pz);
        result.orientation = new Quaternion(qx, qy, qz, qw);
        return result.flipZ();
    }
}

public class CameraTestServer: MonoBehaviour {

    private TcpListener tcpListener; 
    private Thread tcpListenerThread; 
    private TcpClient connectedTcpClient;
    
    private OVRPose? calibratedCameraPose = null;

    private OVRPose cameraPose = OVRPose.identity;

    public void Start() {
        // Consider starting the thread on OnEnable() and ending the Thread on OnDisable()
        tcpListenerThread = new Thread (new ThreadStart(ListenForIncomingRequests));         
        tcpListenerThread.IsBackground = true;         
        tcpListenerThread.Start();     
    }

    public void Update()
    {
        if (!calibratedCameraPose.HasValue)
        {
            if (!OVRPlugin.Media.GetInitialized())
                return;
            OVRPlugin.CameraIntrinsics cameraIntrinsics;
            OVRPlugin.CameraExtrinsics cameraExtrinsics;
            OVRPlugin.GetMixedRealityCameraInfo(0, out cameraExtrinsics, out cameraIntrinsics);
            calibratedCameraPose = cameraExtrinsics.RelativePose.ToOVRPose();
        }

        // The receivedCameraPose is relative to the original calibrated pose, which is itself expressed in stage space.
        OVRPose cameraStagePose = calibratedCameraPose.Value * cameraPose;

        // Override the MRC camera's stage pose
        OVRPlugin.OverrideExternalCameraStaticPose(0, true, cameraStagePose.ToPosef());
    }  

    public const int MaxBufferLength = 65536;
    private void ListenForIncomingRequests () {         
        try {
            tcpListener = new TcpListener(IPAddress.Any, 1337);             
            tcpListener.Start();              
            Debug.Log("[CAMERA SERVER] Server is listening");              
            
            byte[][] receivedBuffers = { new byte[CameraTestServer.MaxBufferLength], new byte[CameraTestServer.MaxBufferLength] };
            int receivedBufferIndex = 0;
            int receivedBufferDataSize = 0;

            while (true) {                 
                using (connectedTcpClient = tcpListener.AcceptTcpClient()) {

                    using (NetworkStream stream = connectedTcpClient.GetStream()) {                         
                        int length; 

                        int maximumDataSize = CameraTestServer.MaxBufferLength - receivedBufferDataSize;

                        while ((length = stream.Read(receivedBuffers[receivedBufferIndex], receivedBufferDataSize, maximumDataSize)) != 0) {                         

                            receivedBufferDataSize += length;

                            while (receivedBufferDataSize >= CameraPayload.StructSize) {
                                CameraPayload payload = CameraPayload.FromBytes(receivedBuffers[receivedBufferIndex]);

                                if (payload.protocolIdentifier != CameraPayload.identifier) {
                                    Debug.LogWarning("Header mismatch");
                                    stream.Close();
                                    connectedTcpClient.Close();
                                    return;
                                }

                                // Consider adding a lock
                                cameraPose = payload.ToPose();

                                int newBufferIndex = 1 - receivedBufferIndex;
                                int newBufferDataSize = receivedBufferDataSize - CameraPayload.StructSize;

                                if (newBufferDataSize > 0) {
                                    Array.Copy(receivedBuffers[receivedBufferIndex], CameraPayload.StructSize, receivedBuffers[newBufferIndex], 0, newBufferDataSize);
                                }
                                receivedBufferIndex = newBufferIndex;
                                receivedBufferDataSize = newBufferDataSize;
                            }
                            
                            maximumDataSize = CameraTestServer.MaxBufferLength - receivedBufferDataSize;
                        }                                             
                    }                 
                }             
            }         
        }         
        catch (SocketException socketException) {             
            Debug.Log("SocketException " + socketException.ToString());         
        }     
    }
}
