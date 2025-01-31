using System;
using System.Collections.Generic;
using System.Linq;
using Antagonists;
using Messages.Server;
using UnityEngine;
using Mirror;
using UI.CharacterCreator;

/// Comfy place to get players and their info (preferably via their connection)
/// Has limited scope for clients (ClientConnectedPlayers only), sweet things are mostly for server
public partial class PlayerList : NetworkBehaviour
{
	//ConnectedPlayer list, server only
	private List<ConnectedPlayer> loggedIn = new List<ConnectedPlayer>();
	public List<ConnectedPlayer> loggedOff = new List<ConnectedPlayer>();

	//For client needs: updated via UpdateConnectedPlayersMessage, useless for server
	public List<ClientConnectedPlayer> ClientConnectedPlayers = new List<ClientConnectedPlayer>();

	public static PlayerList Instance;
	public int ConnectionCount => loggedIn.Count;
	public int OfflineConnCount => loggedOff.Count;
	public List<ConnectedPlayer> InGamePlayers => loggedIn.FindAll(player => player.Script != null);

	public List<ConnectedPlayer> NonAntagPlayers =>
		loggedIn.FindAll(player => player.Script != null && !player.Script.mind.IsAntag);

	public List<ConnectedPlayer> AntagPlayers =>
		loggedIn.FindAll(player => player.Script != null && player.Script.mind.IsAntag);

	public List<ConnectedPlayer> AllPlayers =>
		loggedIn.FindAll(player => (player.Script != null || player.ViewerScript != null));

	/// <summary>
	/// Players in the pre-round lobby who have clicked the ready button and have up to date CharacterSettings
	/// </summary>
	public List<ConnectedPlayer> ReadyPlayers { get; } = new List<ConnectedPlayer>();

	/// <summary>
	/// Used to track who killed who. Could be used to check that a player actually killed someone themselves.
	/// </summary>
	public Dictionary<PlayerScript, List<PlayerScript>>
		KillTracker = new Dictionary<PlayerScript, List<PlayerScript>>();

	/// <summary>
	/// Records the last round player count
	/// </summary>
	public static int LastRoundPlayerCount = 0;

	private void Awake()
	{
		if (Instance == null)
		{
			Instance = this;
		}
		else
		{
			Destroy(gameObject);
		}
	}

	void OnEnable()
	{
		EventManager.AddHandler(EVENT.RoundEnded, SetEndOfRoundPlayerCount);
	}

	void OnDisable()
	{
		EventManager.RemoveHandler(EVENT.RoundEnded, SetEndOfRoundPlayerCount);
	}

	private void SetEndOfRoundPlayerCount()
	{
		LastRoundPlayerCount = Instance.ConnectionCount;
	}

	public override void OnStartServer()
	{
		base.OnStartServer();
		InitAdminController();
	}

	/// <summary>
	/// Called on the server when a kill is confirmed to track which players killed eachother.
	/// </summary>
	[Server]
	public void TrackKill(GameObject perpetrator, GameObject victim)
	{
		var perPlayer = perpetrator?.Player()?.Script;
		var victimPlayer = victim?.Player()?.Script;

		if (perPlayer == null || victimPlayer == null)
		{
			return;
		}

		// Check if the victim list needs to be created
		if (KillTracker.ContainsKey(perPlayer))
		{
			KillTracker[perPlayer].Add(victimPlayer);
		}
		else
		{
			KillTracker[perPlayer] = new List<PlayerScript>()
			{
				victimPlayer
			};
		}
	}

	/// <summary>
	/// Get all players currently located on provided matrix
	/// </summary>
	public List<ConnectedPlayer> GetPlayersOnMatrix(MatrixInfo matrix)
	{
		return InGamePlayers.FindAll(p => (p.Script != null) && p.Script.registerTile.Matrix.Id == matrix?.Id);
	}

	public List<ConnectedPlayer> GetAlivePlayers(List<ConnectedPlayer> players = null)
	{
		if (players == null)
		{
			players = InGamePlayers;
		}

		return players.FindAll(p => !p.Script.IsGhost && p.Script.playerMove.allowInput);
	}

	/// Don't do this unless you realize the consequences
	[Server]
	public void Clear()
	{
		loggedIn.Clear();
	}

	/// <summary>
	/// Set this user's controlled game object to newGameObject (which may be a ghost or a body)
	/// </summary>
	/// <param name="conn">connection whose object should be updated</param>
	/// <param name="newGameObject">new game object they are controlling (should be a ghost or a body)</param>
	[Server]
	public void UpdatePlayer(NetworkConnection conn, GameObject newGameObject)
	{
		ConnectedPlayer connectedPlayer = Get(conn);
		connectedPlayer.GameObject = newGameObject;
		CheckRcon();
	}

	//filling a struct without connections and gameobjects for client's ClientConnectedPlayers list
	public List<ClientConnectedPlayer> ClientConnectedPlayerList =>
		loggedIn.Aggregate(new List<ClientConnectedPlayer>(), (list, player) =>
		{
			//not including headless server player
			if (!GameData.IsHeadlessServer || player.Connection != ConnectedPlayer.Invalid.Connection)
			{
				list.Add(new ClientConnectedPlayer {Name = player.Name, Job = player.Job});
			}

			return list;
		});

	/// <summary>
	/// Adds this connected player to the list, or updates an existing entry if there's already one for
	/// this player's networkconnection. Returns the ConnectedPlayer that was added or updated.
	/// </summary>
	/// <param name="player"></param>
	[Server]
	public ConnectedPlayer AddOrUpdate(ConnectedPlayer player)
	{
		if (player.Equals(ConnectedPlayer.Invalid))
		{
			Logger.Log("Refused to add invalid connected player to this server's player list", Category.Connections);
			return player;
		}

		Logger.Log($"Player {player.Username}'s client ID is: {player.ClientId}.", Category.Connections);

		var loggedOffClient = GetLoggedOffClient(player.ClientId);
		if (loggedOffClient != null)
		{
			Logger.Log(
				$"ConnectedPlayer Username({player.Username}) already exists in this server's PlayerList as Character({loggedOffClient.Name}) " +
				$"Will update existing player instead of adding this new connected player.", Category.Connections);

			if (loggedOffClient.GameObject == null)
			{
				Logger.LogFormat(
					$"The existing ConnectedPlayer contains a null GameObject reference. Removing the entry", Category.Connections);
				loggedOff.Remove(loggedOffClient);
				return player;
			}

			// Switching over to the old player's character is handled by JoinedViewer.
		}

		loggedIn.Add(player);
		Logger.LogFormat("Added to this server's PlayerList {0}. Total:{1}; {2}", Category.Connections, player,
			loggedIn.Count, string.Join(";", loggedIn));
		CheckRcon();
		return player;
	}

	[Server]
	private void TryMoveClientToOfflineList(ConnectedPlayer player)
	{
		if (!loggedIn.Contains(player))
		{
			Logger.Log($"Player with name {player.Name} was not found in online player list. " +
					$"Verifying player lists for integrity...", Category.Connections);
			ValidatePlayerListRecords();
			return;
		}

		Logger.Log($"Added {player.Name} to offline player list.", Category.Connections);
		loggedOff.Add(player);
		loggedIn.Remove(player);
		UpdateConnectedPlayersMessage.Send();
		CheckRcon();
	}

	[Server]
	public bool ContainsConnection(NetworkConnection connection)
	{
		return !Get(connection).Equals(ConnectedPlayer.Invalid);
	}

	[Server]
	public ConnectedPlayer GetLoggedOffClient(string clientID)
	{
		var index = loggedOff.FindIndex(x => x.ClientId == clientID);
		if (index != -1)
		{
			return loggedOff[index];
		}

		return null;
	}

	[Server]
	public bool ContainsName(string name, string _UserId)
	{
		var Character = Get(name);
		if (Character.Equals(ConnectedPlayer.Invalid)) return false;

		return Character.UserId != _UserId;
	}

	[Server]
	public bool ContainsGameObject(GameObject gameObject)
	{
		return !Get(gameObject).Equals(ConnectedPlayer.Invalid);
	}

	[Server]
	public ConnectedPlayer Get(NetworkConnection byConnection)
	{
		return getInternal(player => player.Connection == byConnection);
	}

	[Server]
	public ConnectedPlayer Get(string byName)
	{
		return getInternal(player => player.Name == byName);
	}

	[Server]
	public ConnectedPlayer Get(GameObject byGameObject)
	{
		return getInternal(player => player.GameObject == byGameObject);
	}

	[Server]
	public bool IsAntag(GameObject playerObj)
	{
		var conn = Get(playerObj);
		if (conn == null || conn.Script == null || conn.Script.mind == null) return false;
		return conn.Script.mind.IsAntag;
	}

	[Server]
	public ConnectedPlayer GetByUserID(string byUserID)
	{
		return getInternal(player => player.UserId == byUserID);
	}

	[Server]
	public ConnectedPlayer GetByConnection(NetworkConnection connection)
	{
		return getInternal(player => player.Connection == connection);
	}

	[Server]
	public List<ConnectedPlayer> GetAllByUserID(string byUserID, bool includeOffline  = false)
	{
		var newone = loggedIn.ToList();
		if (includeOffline)
		{
			newone.AddRange(loggedOff);
		}

 		return newone.FindAll(player => player.UserId == byUserID);
	}

	private ConnectedPlayer getInternal(Func<ConnectedPlayer, bool> condition)
	{
		for (var i = 0; i < loggedIn.Count; i++)
		{
			if (condition(loggedIn[i]))
			{
				return loggedIn[i];
			}
		}

		return ConnectedPlayer.Invalid;
	}

	[Server]
	public void RemoveByConnection(NetworkConnection connection)
	{
		if (connection?.address == null || connection.identity == null)
		{
			Logger.Log($"Unknown player disconnected: verifying playerlists for integrity - connection, its address and identity was null.", Category.Connections);
			ValidatePlayerListRecords();
			return;
		}

		var player = Get(connection);
		if (player.Equals(ConnectedPlayer.Invalid))
		{
			Logger.Log($"Unknown player disconnected: verifying playerlists for integrity - connected player was invalid. " +
					$"IP: {connection.address}. Name: {connection.identity.name}.", Category.Connections);
			ValidatePlayerListRecords();
			return;
		}

		SetPlayerReady(player, false);
		CheckForLoggedOffAdmin(player.UserId, player.Username);
		TryMoveClientToOfflineList(player);
	}

	/// <summary>
	/// Verify the data of the player lists
	/// This is good to do if something unexpected has happened
	/// </summary>
	void ValidatePlayerListRecords()
	{
		//verify loggedIn clients:
		for (int i = loggedIn.Count - 1; i >= 0; i--)
		{
			if (loggedIn[i].Connection == null || loggedIn[i].Equals(ConnectedPlayer.Invalid))
			{
				TryMoveClientToOfflineList(loggedIn[i]);
			}
		}

		//verify loggedOff clients:
		for (int i = loggedOff.Count - 1; i >= 0; i--)
		{
			if (loggedOff[i].Equals(ConnectedPlayer.Invalid))
			{
				loggedOff.RemoveAt(i);
				continue;
			}

			if (loggedOff[i].GameObject == null)
			{
				loggedOff.RemoveAt(i);
				continue;
			}
		}
	}

	[Server]
	private void CheckRcon()
	{
		if (RconManager.Instance != null)
		{
			RconManager.UpdatePlayerListRcon();
		}
	}

	[Server]
	public GameObject TakeLoggedOffPlayerbyUserId(string userId)
	{
		Logger.LogTraceFormat("Searching for logged off players with userId {0}", Category.Connections, userId);
		foreach (var player in loggedOff)
		{
			Logger.LogTraceFormat("Found logged off player with userId {0}", Category.Connections, player.UserId);
			if (player.UserId == userId)
			{
				loggedOff.Remove(player);
				return player.GameObject;
			}
		}

		return null;
	}

	[Server]
	public GameObject TakeLoggedOffPlayerbyClientId(string ClientId)
	{
		Logger.LogTraceFormat("Searching for logged off players with userId {0}", Category.Connections, ClientId);
		foreach (var player in loggedOff)
		{
			if (player.ClientId == ClientId)
			{
				Logger.LogTraceFormat("Found logged off player with userId {0}", Category.Connections, player.ClientId);
				loggedOff.Remove(player);
				return player.GameObject;
			}
		}

		return null;
	}

	[Server]
	public void UpdateLoggedOffPlayer(GameObject newBody, GameObject oldBody)
	{
		for (int i = 0; i < loggedOff.Count; i++)
		{
			var player = loggedOff[i];
			if (player.GameObject == oldBody)
			{
				player.GameObject = newBody;
			}
		}
	}

	private void OnDestroy()
	{
		if (adminListWatcher != null)
		{
			adminListWatcher.Changed -= LoadCurrentAdmins;
			adminListWatcher.Dispose();
		}
	}

	/// <summary>
	/// Makes a player ready/unready for job allocations
	/// </summary>
	public void SetPlayerReady(ConnectedPlayer player, bool isReady, CharacterSettings charSettings = null)
	{
		if (isReady)
		{
			// Update connection with locked in job prefs
			if (charSettings != null)
			{
				player.CharacterSettings = charSettings;
			}
			else
			{
				Logger.LogError($"{player.Username} was set to ready with NULL character settings:\n{player}", Category.Round);
			}
			ReadyPlayers.Add(player);
			Logger.Log($"Set {player.Username} to ready with these character settings:\n{charSettings}", Category.Round);
		}
		else
		{
			ReadyPlayers.Remove(player);
			Logger.Log($"Set {player.Username} to NOT ready!", Category.Round);
		}
	}

	/// <summary>
	/// Clears the list of ready players
	/// </summary>
	[Server]
	public void ClearReadyPlayers()
	{
		ReadyPlayers.Clear();
	}

	public static bool HasAntagEnabled(AntagPrefsDict antagPrefs, Antagonist antag)
	{
		return !antag.ShowInPreferences ||
		       (antagPrefs.ContainsKey(antag.AntagName) && antagPrefs[antag.AntagName]);
	}

	public static bool HasAntagEnabled(ConnectedPlayer connectedPlayer, Antagonist antag)
	{
		if (connectedPlayer.CharacterSettings == null)
		{
			if (connectedPlayer.Script.characterSettings == null) return false;

			connectedPlayer.CharacterSettings = connectedPlayer.Script.characterSettings;
		}

		return !antag.ShowInPreferences ||
		       (connectedPlayer.CharacterSettings.AntagPreferences.ContainsKey(antag.AntagName)
		        && connectedPlayer.CharacterSettings.AntagPreferences[antag.AntagName]);
	}
}

[Serializable]/// Minimalistic connected player information that all clients can posess
public struct ClientConnectedPlayer
{
	public string UserName;
	public string Name;
	public JobType Job;
	public bool PendingSpawn;
	public string Tag;

	public override string ToString()
	{
		return $"[{nameof(Name)}='{Name}', {nameof(Job)}={Job}]";
	}
}
