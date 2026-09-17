using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WebUI {
    public sealed class DebugCubeController : MonoBehaviour {
        public ChromiumOverlay Overlay;
        public Camera View;
        public Transform Cube;
        public bool Dragging {get;private set;}
        public int MenuClicks {get;private set;}
        public bool PageReady {get;private set;}
        Vector2 previous;
        Quaternion initial;
        BrowserSession bound;
        Material surface;
        void Awake(){
            initial=Cube.rotation;
            surface=new Material(Shader.Find("Standard"));surface.color=new Color(.15f,.72f,.85f);
            surface.SetFloat("_Metallic",.25f);surface.SetFloat("_Glossiness",.55f);
            Cube.GetComponent<Renderer>().material=surface;
        }
        void Update(){
            // Keep the cube in the right half as the window/aspect ratio changes.
            Cube.position=View.ViewportToWorldPoint(new Vector3(.73f,.51f,7));
            if(Overlay.Session!=null&&bound!=Overlay.Session){bound=Overlay.Session;bound.Message+=OnMessage;}
            if(Input.GetMouseButtonDown(0))TryBeginDrag(Input.mousePosition);
            if(Dragging&&Input.GetMouseButton(0))DragTo(Input.mousePosition);
            if(Input.GetMouseButtonUp(0))EndDrag();
        }
        public bool TryBeginDrag(Vector2 position){
            if(Overlay.Session==null||Overlay.IsPointerOverUI(position))return false;
            if(!Physics.Raycast(View.ScreenPointToRay(position),out var hit)||hit.transform!=Cube)return false;
            previous=position;Dragging=true;return true;
        }
        public void DragTo(Vector2 position){
            if(!Dragging)return;
            Vector2 delta=position-previous;previous=position;
            Cube.Rotate(View.transform.up,-delta.x*.4f,Space.World);
            Cube.Rotate(View.transform.right,delta.y*.4f,Space.World);
        }
        public void EndDrag()=>Dragging=false;
        void OnApplicationFocus(bool focused){if(!focused)EndDrag();}
        void OnMessage(string name,JToken data){
            if(name=="debugReady"){PageReady=true;return;}
            if(name!="debugMenu")return;++MenuClicks;
            switch((string)data?["action"]){
                case "reset":Cube.rotation=initial;break;
                case "color":surface.color=Color.HSVToRGB(Mathf.Repeat(MenuClicks*.19f,.999f),.65f,.9f);break;
            }
        }
        void OnDisable(){EndDrag();if(bound!=null)bound.Message-=OnMessage;bound=null;}
        void OnDestroy(){if(surface)Destroy(surface);}
    }
}
