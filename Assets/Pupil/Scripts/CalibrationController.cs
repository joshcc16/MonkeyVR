using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static Serial;

namespace PupilLabs
{
    public class CalibrationController : MonoBehaviour
    {
        [Header("Pupil Labs Connection")]
        public SubscriptionsController subsCtrl;
        public TimeSync timeSync;
        public DataController dataController;

        [Header("Scene References")]
        public new Camera camera;
        public Transform marker;
        public GameObject leftMask;
        public GameObject rightMask;
        public GazeVisualizer gazeVisualizer;
        public GameObject window;
        public GameObject plane;

        [Header("Settings")]
        public CalibrationSettings settings;
        public CalibrationTargets fuseTestTargets;
        public CalibrationTargets targets;
        public bool showPreview;

        [Header("Window and Marker Settings")]
        [Range(1f, 90f)]
        public float xAngle;
        [Range(1f, 90f)]
        public float yAngle;
        [Range(0.005f, 0.1f)]
        public float markerSize;


        public bool IsCalibrating { get { return calibration.IsCalibrating; } }

        //events
        public event Action OnCalibrationStarted;
        public event Action OnCalibrationRoutineDone;
        public event Action OnCalibrationFailed;
        public event Action OnCalibrationSucceeded;
        public event Action OnFuseTestStarted;
        public event Action OnFuseTestComplete;

        //members
        Calibration calibration = new Calibration();
        SerialPort sp;

        [HideInInspector] public int targetIdx;
        int lastIdx;
        int targetSampleCount;
        Vector3 currLocalTargetPos;

        float tLastSample = 0;
        float tLastTarget = 0;
        float tLastTrial = 0;
        float tStartITI = 0;
        float tTotalFix = 0;
        List<GameObject> previewMarkers = new List<GameObject>();
        int[] indices = new int[225];
        int[] trialMode = new int[225];
        [HideInInspector] public int numCorrect;
        [HideInInspector] public int trialNum = 0;
        [HideInInspector] public int mode = 3;
        [HideInInspector] public Vector3 pos;

        //saving discrete data
        int runNumber = PlayerPrefs.GetInt("Fusion Run Number");
        static List<int> trialNumber = new List<int>();
        static List<bool> rewarded = new List<bool>();
        static List<int> eyeMode = new List<int>();
        static List<int> targetIndex = new List<int>();

        bool previewMarkersActive = false;

        //added settings
        bool isAuto;
        float juiceTime;
        float totalTime;
        float ignoreInitialSeconds;
        float secondsPerTarget;
        float intertrialInterval;
        [HideInInspector] public float yThreshold;
        [HideInInspector] public float xThreshold;
        [HideInInspector] public float scale = 1f;
        float sizeX;
        float sizeY;

        //flags
        bool flagCalibrating = false;
        bool flagChangePos = false;
        bool flagFuseTest = false;
        bool flagTesting = false;
        bool flagWait = true;
        [HideInInspector] public bool flagReward = false;
        bool flagFailed = false;

        [HideInInspector] public enum Status
        {
            none = 0,
            calibration = 1,
            fixating = 2,
            test = 3
        }
        [HideInInspector] public Status status;
        readonly static List<int> order = new List<int>()
        {
            3, 6, 7, 8, 5, 2, 1, 0, 3, 7, 5, 1, 0, 6, 8, 2, 4
        };
        int autoIdx = 0;

        System.Random random = new System.Random();

        void OnEnable()
        {
            calibration.OnCalibrationSucceeded += CalibrationSucceeded;
            calibration.OnCalibrationFailed += CalibrationFailed;
            status = Status.none;

            isAuto = PlayerPrefs.GetInt("isAuto") == 1;
            juiceTime = PlayerPrefs.GetFloat("Calibration Juice Time");
            totalTime = PlayerPrefs.GetFloat("Presentation Time");
            ignoreInitialSeconds = PlayerPrefs.GetFloat("Grace Period");
            secondsPerTarget = PlayerPrefs.GetFloat("Fixation Time");
            intertrialInterval = PlayerPrefs.GetFloat("Intertrial Interval");
            xAngle = PlayerPrefs.GetFloat("X Threshold");
            yAngle = PlayerPrefs.GetFloat("Y Threshold");
            xThreshold = Mathf.Tan(xAngle * Mathf.Deg2Rad / 2.0f);
            yThreshold = Mathf.Tan(yAngle * Mathf.Deg2Rad / 2.0f);
            markerSize = PlayerPrefs.GetFloat("Marker Size");

            sizeX = scale * xThreshold;
            sizeY = scale * yThreshold;

            window.transform.localScale = new Vector3(sizeX, sizeY, 0.0f);
            window.gameObject.GetComponent<SpriteRenderer>().enabled = false;
            //sp = new SerialPort("COM4", 2000000);
            //sp.Open();
            //sp.ReadTimeout = 1;

            //print(isAuto);

            //subsCtrl.SubscribeTo("calibration", CustomReceiveData);

            marker.localScale = markerSize * Vector3.one;

            bool allReferencesValid = true;
            if (subsCtrl == null)
            {
                Debug.LogError("SubscriptionsController reference missing!");
                allReferencesValid = false;
            }
            if (timeSync == null)
            {
                Debug.LogError("TimeSync reference missing!");
                allReferencesValid = false;
            }
            if (marker == null)
            {
                Debug.LogError("Marker reference missing!");
                allReferencesValid = false;
            }
            if (camera == null)
            {
                Debug.LogError("Camera reference missing!");
                allReferencesValid = false;
            }
            if (settings == null)
            {
                Debug.LogError("CalibrationSettings reference missing!");
                allReferencesValid = false;
            }
            if (targets == null)
            {
                Debug.LogError("CalibrationTargets reference missing!");
                allReferencesValid = false;
            }
            if (!allReferencesValid)
            {
                Debug.LogError("CalibrationController is missing required references to other components. Please connect the references, or the component won't work correctly.");
                enabled = false;
                return;
            }

            InitPreviewMarker();
            SetPreviewMarkers(false);
        }

        void CustomReceiveData(string topic, Dictionary<string, object> dictionary, byte[] thirdFrame = null)
        {
            Debug.Log(dictionary["result"].ToString());
        }

        void OnDisable()
        {
            //sp.Close();

            calibration.OnCalibrationSucceeded -= CalibrationSucceeded;
            calibration.OnCalibrationFailed -= CalibrationFailed;

            if (calibration.IsCalibrating)
            {
                StopCalibration();
            }
        }

        private void Start()
        {
            sp = serial.sp;
            SceneManager.activeSceneChanged += OnSceneChange;
        }

        void OnSceneChange(Scene prev, Scene next)
        {
            this.gameObject.SetActive(false);
        }

        void Update()
        {
            float gazeX = gazeVisualizer.projectionMarker.position.x;
            float gazeY = gazeVisualizer.projectionMarker.position.y;

            //print(fov);

            window.transform.localScale = new Vector3(Mathf.Tan(xAngle * Mathf.Deg2Rad / 2.0f) * 2.0f * scale, Mathf.Tan(yAngle * Mathf.Deg2Rad / 2.0f) * 2.0f * scale, 1f);
            marker.localScale = markerSize * Vector3.one * scale;

            xThreshold = Mathf.Tan(xAngle * Mathf.Deg2Rad / 2.0f ) * scale;
            yThreshold = Mathf.Tan(yAngle * Mathf.Deg2Rad / 2.0f ) * scale;

            //Vector3 pos = Vector3.zero;

            if (calibration.IsCalibrating || flagFuseTest)
            {
                pos = marker.transform.position;
            }
            else
            {
                //UpdatePreviewMarkers();
                pos = previewMarkers[targetIdx].transform.position;
            }

            //print(string.Format("X: {0}, XT: {1}, Y: {2}, YT: {3}", gazeX, pos.x, gazeY, pos.y));

            if (Mathf.Abs(gazeX - pos.x) <= xThreshold && Mathf.Abs(gazeY - pos.y) <= yThreshold)
            {
                gazeVisualizer.projectionMarker.GetComponent<MeshRenderer>().material.color = Color.green;
            }
            else
            {
                gazeVisualizer.projectionMarker.GetComponent<MeshRenderer>().material.color = Color.red;
            }

            if (showPreview != previewMarkersActive)
            {
                //SetPreviewMarkers(showPreview);
                if (flagChangePos && !calibration.IsCalibrating)
                {
                    previewMarkers[lastIdx].SetActive(false);
                    previewMarkers[targetIdx].SetActive(true);
                    window.gameObject.GetComponent<SpriteRenderer>().enabled = true;

                    flagChangePos = !flagChangePos;
                }

                lastIdx = targetIdx;
            }

            if (calibration.IsCalibrating)
            {
                UpdateCalibration();
            }
            else if (flagFuseTest)
            {
                UpdateFuseTest();
            }

            if (Input.GetKeyUp(KeyCode.C))
            {
                ToggleCalibration();
            }
            else if (Input.GetKeyDown(KeyCode.P))
            {
                if (showPreview)
                {
                    SetPreviewMarkers(!showPreview);
                }

                showPreview = !showPreview;
            }
            else if (Input.GetKeyDown(KeyCode.N))
            {
                SceneManager.LoadScene(2);
            }
            else if (Input.GetKeyDown(KeyCode.F))
            {
                ToggleFuseTest();
            }
            else if (!flagCalibrating)
            {
                if (Input.GetKeyDown(KeyCode.Keypad1))
                {
                    targetIdx = 0;
                    flagChangePos = !flagChangePos;
                }
                else if (Input.GetKeyDown(KeyCode.Keypad2))
                {
                    targetIdx = 1;
                    flagChangePos = !flagChangePos;
                }
                else if (Input.GetKeyDown(KeyCode.Keypad3))
                {
                    targetIdx = 2;
                    flagChangePos = !flagChangePos;
                }
                else if (Input.GetKeyDown(KeyCode.Keypad4))
                {
                    targetIdx = 3;
                    flagChangePos = !flagChangePos;
                }
                else if (Input.GetKeyDown(KeyCode.Keypad5))
                {
                    targetIdx = 4;
                    flagChangePos = !flagChangePos;
                }
                else if (Input.GetKeyDown(KeyCode.Keypad6))
                {
                    targetIdx = 5;
                    flagChangePos = !flagChangePos;
                }
                else if (Input.GetKeyDown(KeyCode.Keypad7))
                {
                    targetIdx = 6;
                    flagChangePos = !flagChangePos;
                }
                else if (Input.GetKeyDown(KeyCode.Keypad8))
                {
                    targetIdx = 7;
                    flagChangePos = !flagChangePos;
                }
                else if (Input.GetKeyDown(KeyCode.Keypad9))
                {
                    targetIdx = 8;
                    flagChangePos = !flagChangePos;
                }

                if (targetIdx>8)
                {
                    targetIdx=0;
                }

                if (!calibration.IsCalibrating && !flagFuseTest)
                {
                    window.transform.position = previewMarkers[targetIdx].transform.position;
                }
            }
        }

        public void ToggleFuseTest()
        {
            if (!flagFuseTest)
            {
                OnFuseTestStarted();
                trialNum = 0;
                StartFuseTest();

                status = Status.test;

                flagFuseTest = true;
                flagTesting = true;
            }
            else
            {
                OnFuseTestComplete();
                StopFuseTest();

                status = Status.none;

                flagFuseTest = false;
                flagTesting = false;
            }
        }

        public void StartFuseTest()
        {
            Debug.Log("Starting Fuse Test.");

            showPreview = false;

            foreach (GameObject marker in previewMarkers)
            {
                marker.SetActive(false);
            }

            var idx = 0;

            int totalTrials = fuseTestTargets.GetTargetCount();

            for (int i = 0; i < fuseTestTargets.GetTargetCount(); i++)
            {
                var n = 0;
                for (int j = 0; j < totalTrials; j++)
                {
                    indices[idx] = i;
                    if (j < totalTrials / 3)
                    {
                        n = 0;
                    }
                    else if (j >= totalTrials / 3 && j < totalTrials * 2 / 3)
                    {
                        n = 1;
                    }
                    else
                    {
                        n = 2;
                    }
                    trialMode[idx] = n;

                    idx++;

                }
            }

            System.Random rand = new System.Random();
            for (int i = indices.Length - 1; i > 0; i--)
            {
                int swapIdx = rand.Next(i + 1);
                int temp = indices[i];
                int tempN = trialMode[i];
                indices[i] = indices[swapIdx];
                trialMode[i] = trialMode[swapIdx];
                indices[swapIdx] = temp;
                trialMode[swapIdx] = tempN;
            }

            //var similarity = 0; // just a measure of how many times equal values are adjacent
            //for (int i = 0; i < indices.Length - 1; i++)
            //{
            //    if (indices[i] == indices[i + 1])
            //    {
            //        similarity += 1;
            //    }
            //}

            //StringBuilder sb = new StringBuilder();

            for (int i = 1; i < indices.Length - 1; i++)
            {
                if (indices[i] == indices[i - 1])
                {
                    int temp = indices[i];
                    int tempN = trialMode[i];
                    indices[i] = indices[i + 1];
                    trialMode[i] = trialMode[i + 1];
                    indices[i + 1] = temp;
                    trialMode[i + 1] = tempN;
                }

                //sb.AppendLine(string.Format("{0}, {1}", indices[i], trialMode[i]));
            }

            //File.WriteAllText("C:\\Users\\lab\\Desktop\\indices_test.txt", sb.ToString());

            trialNum = 0;
            numCorrect = 0;
            mode = trialMode[trialNum];
            targetIdx = indices[trialNum];

            trialNumber.Add(trialNum);
            eyeMode.Add(mode);
            targetIndex.Add(targetIdx);

            tLastTrial = Time.time;

            UpdatePosition();

            marker.gameObject.SetActive(true);
        }

        public void UpdateFuseTest()
        {
            //print(mode);

            UpdateMarker();

            //marker.gameObject.SetActive(flagTesting);

            float tNow = Time.time;
            float gazeX = gazeVisualizer.projectionMarker.position.x;
            float gazeY = gazeVisualizer.projectionMarker.position.y;

            if (tNow - tLastTrial <= totalTime)
            {
                //print("testing");
                if (Mathf.Abs(gazeX - marker.position.x) <= xThreshold
                    && Mathf.Abs(gazeY - marker.position.y) <= yThreshold)
                {
                    //print("in");
                    tTotalFix += tNow - tLastSample;
                }

                tLastSample = tNow;
            }
            else
            {
                if (tTotalFix >= secondsPerTarget)
                {
                    string toSend = "ij" + juiceTime.ToString();
                    try
                    {
                        print("rewarded");
                        flagReward = true;
                        sp.Write(toSend);
                    }
                    catch (Exception e)
                    {
                        Debug.Log(e);
                    }

                    numCorrect++;
                }

                mode = 3;

                if (flagTesting)
                {
                    flagTesting = false;
                    rewarded.Add(flagReward);

                    print(string.Format("trial {0} complete", trialNum));
                    print(string.Format("Total Time: {0}, Target Time: {1}", tTotalFix, secondsPerTarget));

                    tTotalFix = 0.0f;

                    if (isAuto) tStartITI = Time.time;
                }

                if (isAuto && tNow - tStartITI >= intertrialInterval)
                {
                    flagChangePos = true;
                }

                leftMask.SetActive(false);
                rightMask.SetActive(false);
                marker.gameObject.GetComponent<SpriteRenderer>().enabled = false;
                window.gameObject.GetComponent<SpriteRenderer>().enabled = false;

                if (trialNum < 225 && flagChangePos)
                {
                    trialNum++;

                    targetIdx = indices[trialNum];

                    mode = trialMode[trialNum];

                    trialNumber.Add(trialNum);
                    eyeMode.Add(mode);
                    targetIndex.Add(targetIdx);

                    switch (mode)
                    {
                        case 0:
                            leftMask.SetActive(true);
                            rightMask.SetActive(false);
                            break;

                        case 1:
                            leftMask.SetActive(false);
                            rightMask.SetActive(true);
                            break;

                        case 2:
                            leftMask.SetActive(false);
                            rightMask.SetActive(false);
                            break;
                    }

                    tLastTrial = Time.time;

                    UpdatePosition();

                    flagReward = false;

                    flagChangePos = !flagChangePos;

                    flagTesting = !flagTesting;

                    flagWait = !flagWait;
                }
                
                if (trialNum >= 225)
                {
                    ToggleFuseTest();
                }
            }
        }

        public void StopFuseTest()
        {
            Debug.Log("Stopping Fuse Test.");

            marker.gameObject.SetActive(false);

            Save();
        }

        public void ToggleCalibration()
        {
            if (calibration.IsCalibrating)
            {
                scale = 1f;

                flagCalibrating = false;
                StopCalibration();

                targetIdx = 4;

                status = Status.fixating;
            }
            else
            {
                SetPreviewMarkers(false);

                scale = 1.5f;

                flagCalibrating = true;
                StartCalibration();

                status = Status.calibration;
            }
        }

        public void StartCalibration()
        {
            plane.transform.position = new Vector3(0, 0, scale);

            if (!enabled)
            {
                Debug.LogWarning("Component not enabled!");
                return;
            }

            if (!subsCtrl.IsConnected)
            {
                Debug.LogWarning("Calibration not possible: not connected!");
                return;
            }

            Debug.Log("Starting Calibration");

            showPreview = false;

            trialNum = 0;
            numCorrect = 0;

            targetIdx = 4;
            targetSampleCount = 0;

            autoIdx = 0;

            flagReward = false;
            flagFailed = false;

            UpdatePosition();

            marker.gameObject.SetActive(true);

            calibration.StartCalibration(settings, subsCtrl);
            Debug.Log($"Sample Rate: {settings.SampleRate}");

            if (OnCalibrationStarted != null)
            {
                OnCalibrationStarted();
            }

            //abort process on disconnecting
            subsCtrl.OnDisconnecting += StopCalibration;
        }

        public void StopCalibration()
        {
            plane.transform.position = new Vector3(0, 0, scale);

            if (!calibration.IsCalibrating)
            {
                Debug.Log("Nothing to stop.");
                return;
            }

            calibration.StopCalibration();

            marker.gameObject.SetActive(false);

            if (OnCalibrationRoutineDone != null)
            {
                OnCalibrationRoutineDone();
            }

            subsCtrl.OnDisconnecting -= StopCalibration;
        }

        void OnApplicationQuit()
        {
            calibration.Destroy();
        }

        private void UpdateCalibration()
        {
            UpdateMarker();

            float tNow = Time.time;
            float gazeX = gazeVisualizer.projectionMarker.position.x;
            float gazeY = gazeVisualizer.projectionMarker.position.y;

            if (tNow - tLastSample >= 1f / settings.SampleRate - Time.deltaTime / 2f)
            {

                if (tNow - tLastTarget < ignoreInitialSeconds - Time.deltaTime / 2f)
                {
                    return;
                }

                if (Mathf.Abs(gazeX - marker.position.x) <= xThreshold 
                    && Mathf.Abs(gazeY - marker.position.y) <= yThreshold
                    && flagCalibrating)
                {
                    //flagCalibrating = !flagCalibrating;

                    //marker.gameObject.GetComponent<SpriteRenderer>().enabled = false;

                    //flagReward = false;
                    //flagFailed = true;

                    //print("failed");

                    //if (isAuto) tStartITI = Time.time;

                    

                    tTotalFix += tNow - tLastSample;
                }

                //Adding the calibration reference data to the list that will be passed on, once the required sample amount is met.
                double sampleTimeStamp = timeSync.ConvertToPupilTime(Time.realtimeSinceStartup);
                AddSample(sampleTimeStamp);

                targetSampleCount++; //Increment the current calibration sample. (Default sample amount per calibration point is 120)

                tLastSample = tNow;

                if (tNow - tLastTarget >= totalTime)
                {
                    if (tTotalFix >= secondsPerTarget)
                    {
                        string toSend = "ij" + juiceTime.ToString();
                        try
                        {
                            flagReward = true;
                            sp.Write(toSend);
                        }
                        catch(Exception e)
                        {
                            Debug.Log(e);
                        }

                        numCorrect++;

                        tTotalFix = 0.0f;
                    }

                    if (flagCalibrating)
                    {
                        flagCalibrating = !flagCalibrating;

                        marker.gameObject.GetComponent<SpriteRenderer>().enabled = false;

                        if (isAuto) tStartITI = Time.time;
                    }

                    //print(tNow - tStartITI >= intertrialInterval);

                    if (isAuto && tNow - tStartITI >= intertrialInterval)
                    {
                        if (autoIdx < 17)
                        {
                            print("auto");
                            autoIdx++;
                            targetIdx = order[autoIdx]; 
                            tStartITI = Time.time;
                        }
                        else
                        {
                            ToggleCalibration();

                            return;
                        }

                        flagChangePos = true;
                    }

                    if (flagChangePos)
                    {
                        UpdatePosition();

                        calibration.SendCalibrationReferenceData();

                        targetSampleCount = 0;

                        flagChangePos = !flagChangePos;

                        trialNum++;

                        lastIdx = targetIdx;

                        flagCalibrating = !flagCalibrating;
                        flagReward = false;
                        flagFailed = false;
                    }
                }
            }
        }

        private void CalibrationSucceeded()
        {
            if (OnCalibrationSucceeded != null)
            {
                OnCalibrationSucceeded();
            }
        }

        private void CalibrationFailed()
        {
            if (OnCalibrationFailed != null)
            {
                OnCalibrationFailed();
            }
        }

        private void AddSample(double time)
        {
            float[] refData;

            refData = new float[] { currLocalTargetPos.x, currLocalTargetPos.y, currLocalTargetPos.z };
            refData[1] /= camera.aspect;

            for (int i = 0; i < refData.Length; i++)
            {
                refData[i] *= Helpers.PupilUnitScalingFactor;
            }

            calibration.AddCalibrationPointReferencePosition(refData, time);
        }

        private void UpdatePosition()
        {
            if (!flagFuseTest)
            {
                currLocalTargetPos = targets.GetLocalTargetPosAt(targetIdx);
            }
            else
            {
                currLocalTargetPos = fuseTestTargets.GetLocalTargetPosAt(targetIdx);
            }

            tLastTarget = Time.time;
        }

        private void UpdateMarker()
        {
            //marker.position = camera.transform.localToWorldMatrix.MultiplyPoint(currLocalTargetPos);
            marker.position = currLocalTargetPos * scale;
            window.transform.position = marker.position;
            //marker.LookAt(camera.transform.position);

            if (flagCalibrating || flagTesting)
            {
                marker.gameObject.GetComponent<SpriteRenderer>().enabled = true;
                window.gameObject.GetComponent<SpriteRenderer>().enabled = true;
            }
            else
            {
                marker.gameObject.GetComponent<SpriteRenderer>().enabled = false;
                window.gameObject.GetComponent<SpriteRenderer>().enabled = false;
            }
        }

        void OnDrawGizmos()
        {
            if (camera == null || targets == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                return;
            }

            Gizmos.matrix = camera.transform.localToWorldMatrix;
            for (int i = 0; i < targets.GetTargetCount(); ++i)
            {
                var target = targets.GetLocalTargetPosAt(i);
                Gizmos.DrawWireSphere(target, 0.035f);
            }
        }

        void InitPreviewMarker()
        {
            var previewMarkerParent = new GameObject("Calibration Targets Preview");
            previewMarkerParent.transform.SetParent(camera.transform);
            previewMarkerParent.transform.localPosition = Vector3.zero;
            previewMarkerParent.transform.localRotation = Quaternion.identity;

            for (int i = 0; i < targets.GetTargetCount(); ++i)
            {
                var target = targets.GetLocalTargetPosAt(i);
                var previewMarker = Instantiate<GameObject>(marker.gameObject);
                previewMarker.transform.parent = previewMarkerParent.transform;
                previewMarker.transform.localPosition = target;
                //previewMarker.transform.LookAt(camera.transform.position);
                previewMarker.SetActive(true);
                previewMarkers.Add(previewMarker);
            }

            previewMarkersActive = true;
        }

        public void UpdatePreviewMarkers()
        {
            for (int i = 0; i < targets.GetTargetCount(); ++i)
            {
                var target = targets.GetLocalTargetPosAt(i) * scale;
                previewMarkers[i].transform.localScale = Vector3.one * markerSize * scale;
                previewMarkers[i].transform.localPosition = new Vector3(target.x, target.y, scale);
            }
        }


        void SetPreviewMarkers(bool value)
        {
            foreach (var marker in previewMarkers)
            {
                marker.SetActive(value);
            }

            previewMarkersActive = value;
        }

        private void Save()
        {
            string firstLine = "trial,eye,target,rewarded";

            List<int> temp;

            StringBuilder csvDisc = new StringBuilder();

            csvDisc.AppendLine(firstLine);

            temp = new List<int>()
            {
                trialNumber.Count,
                eyeMode.Count,
                targetIndex.Count,
                rewarded.Count
            };

            temp.Sort();

            for (int i = 0; i < temp[0]; i++)
            {
                var line = string.Format("{0},{1},{2},{3}",
                    trialNumber[i],
                    eyeMode[i],
                    targetIndex[i],
                    rewarded[i]);
                csvDisc.AppendLine(line);
            }

            string path = PlayerPrefs.GetString("Path");

            string discPath = path + "/fusion_discontinuous_data_" + PlayerPrefs.GetString("Name") + "_" + DateTime.Today.ToString("MMddyyyy") + "_" + runNumber + ".txt";
            File.WriteAllText(discPath, csvDisc.ToString());

            runNumber++;

            PlayerPrefs.SetInt("Fusion Run Number", runNumber);
        }
    }
}
