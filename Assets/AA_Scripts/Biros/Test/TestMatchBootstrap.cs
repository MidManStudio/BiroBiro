using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Biros.Core;
using Biros.Gameplay;

public class TestMatchBootstrap : MonoBehaviour
{
    [SerializeField] private GameObject _penPrefab;
    [SerializeField] private Transform _spawnA;
    [SerializeField] private Transform _spawnB;

    private void Start()
    {
        // For testing: auto-host and start immediately in editor
        NetworkManager.Singleton.StartHost();
        StartCoroutine(WaitThenStart());
    }

    private IEnumerator WaitThenStart()
    {
        // Wait for MatchStateManager NetworkObject to spawn
        yield return new WaitUntil(() => MatchStateManager.Instance != null &&
                                         MatchStateManager.Instance.IsSpawned);

        // Spawn pen A for slot 0 (local host)
        var penA = Instantiate(_penPrefab, _spawnA.position, _spawnA.rotation);
        penA.GetComponent<NetworkObject>().Spawn();
        penA.GetComponent<PenController>().ServerInitialize(
            0, NetworkManager.Singleton.LocalClientId, null);

        // In a real match a second client would connect; for solo testing
        // register the host as both slots so the state machine can loop.
        var penB = Instantiate(_penPrefab, _spawnB.position, _spawnB.rotation);
        penB.GetComponent<NetworkObject>().Spawn();
        penB.GetComponent<PenController>().ServerInitialize(
            1, NetworkManager.Singleton.LocalClientId, null);

        var players = new List<ulong> { NetworkManager.Singleton.LocalClientId,
                                         NetworkManager.Singleton.LocalClientId };
        MatchStateManager.Instance.ServerStartMatch(players);
    }
}