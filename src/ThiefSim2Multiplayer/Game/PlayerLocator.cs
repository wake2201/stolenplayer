using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class PlayerLocator
    {
        private readonly Type playerType = Type.GetType("PlayerScript, Assembly-CSharp", false);
        private readonly Type motorType = Type.GetType("PlayerMotorBehavior, Assembly-CSharp", false);
        private readonly Type cameraType = Type.GetType("PlayerCameraBehavior, Assembly-CSharp", false);
        private readonly Type managerType = Type.GetType("GameManager, Assembly-CSharp", false);
        private Component cached;

        internal Component Find()
        {
            if (playerType == null || motorType == null || cameraType == null) return null;
            if (Valid(cached)) return cached;
            Scene scene = SceneManager.GetSceneByName("MainScene");
            if (!scene.IsValid() || !scene.isLoaded) return null;
            // The verified canonical GameManager.PS remains the actor when vehicle
            // parenting takes it out of its original MainScene hierarchy.
            if (managerType != null)
            {
                Component manager = null;
                foreach (GameObject root in scene.GetRootGameObjects()) foreach (Component candidate in root.GetComponentsInChildren(managerType, true))
                { if (manager) return null; manager = candidate; }
                var actor = manager ? GameAccess.Get(manager, "PS") as Component : null;
                if (actor && actor.GetType() == playerType && Valid(actor)) { cached = actor; return actor; }
            }
            Component result = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Component candidate in root.GetComponentsInChildren(playerType, true))
            {
                if (!candidate || !candidate.gameObject.activeInHierarchy || !candidate.GetComponent<CharacterController>() ||
                    !candidate.GetComponent(motorType) || !candidate.GetComponent(cameraType)) continue;
                // Ambiguity is unsafe: do not select an arbitrary object.
                if (result) return null;
                result = candidate;
            }
            cached = result; return result;
        }
        private bool Valid(Component actor) => actor && actor.gameObject.activeInHierarchy && actor.gameObject.scene.isLoaded &&
            actor.GetComponent<CharacterController>() && actor.GetComponent(motorType) && actor.GetComponent(cameraType);
    }
}
