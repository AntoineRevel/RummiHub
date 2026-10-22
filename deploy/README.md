# Déployer RummiHub (un VPS, Docker Compose)

Site public : `https://rummisolve.com`. `https://www.rummisolve.com` redirige vers l’apex.

Cinq processus, comme l’AppHost Aspire (sans le tableau de bord ni Redis Insight) :

| Service | Rôle | Exposition |
|---|---|---|
| `caddy` | TLS Let's Encrypt, HTTP→HTTPS, WebSocket / SignalR | **80 et 443 seulement** |
| `webfrontend` | Blazor Server, une seule réplique | réseau `internal` |
| `apiservice` | `POST /solve` | réseau `internal` |
| `solver-worker` | 2 répliques, groupe Redis `solvers` | réseau `internal` |
| `cache` | Redis Stack (RedisJSON), volume persistant | réseau `internal` |

L’API, Redis et les workers n’ont aucun port publié. Le réseau `internal` n’a pas de sortie vers Internet. Les parties et les secrets de siège restent en mémoire dans `webfrontend` : ne pas augmenter le nombre de répliques.

La page `/bench` (comparateur de solveurs) répond 404 en production et disparaît du menu : elle lance des calculs lourds dans `webfrontend` et écrit sur le disque. Pour l’activer malgré tout, poser `SolverBench__Enabled: "true"` sur `webfrontend` — à éviter sur un site public.

## Prérequis

- Ubuntu 24.04, Docker et Docker Compose v2 (le plugin `docker compose`).
- DNS : enregistrements A (et AAAA le cas échéant) de `rummisolve.com` **et** `www.rummisolve.com` vers le VPS, avant le premier `up` (Let's Encrypt).
- Accès en lecture au dépôt (privé) : une clé de déploiement SSH en lecture seule, générée sur le VPS et ajoutée dans *Settings → Deploy keys*, avec un alias `github-rummihub` dans `~/.ssh/config` (`HostName github.com`, `IdentityFile` de cette clé, `IdentitiesOnly yes`).
- `ufw` : ouvrir seulement 22, 80 et 443. Ne pas publier 6379 ni le port de l’API.

## Premier déploiement

```bash
git clone git@github-rummihub:AntoineRevel/RummiHub.git
cd RummiHub/deploy
cp .env.example .env
openssl rand -hex 32
```

Coller la valeur dans `SOLVER_API_KEY` (64 caractères hex, sans `$` ni espace). Renseigner `DOMAIN` (`rummisolve.com`) et `ACME_EMAIL`. Puis :

```bash
docker compose up -d --build
docker compose ps
```

Les commandes se lancent **depuis `deploy/`**, pour que Compose lise `.env`.

## Mise à jour

Chaque merge sur `master` déploie tout seul (section suivante). À la main, sur le VPS :

```bash
rummihub-deploy
```

Sans l’installation ci-dessous : `git pull && docker compose up -d --build` depuis `deploy/`.

Les volumes (certificats, AOF Redis, clés Data Protection) sont conservés. Un redémarrage de `webfrontend` efface les parties en cours ; les clés antiforgery, elles, restent sur le volume `dataprotection_keys`. Au démarrage, le journal peut signaler que la clé est stockée sans chiffrement : c’est le comportement par défaut de ASP.NET sur Linux, et le volume n’est pas publié.

## Déploiement automatique

Le workflow `.github/workflows/deploy.yml` lance les tests sur chaque PR. Sur `master`, une fois les tests passés, il se connecte en SSH au VPS avec une clé qui ne peut exécuter qu’une commande : `rummihub-deploy`. Celle-ci démarre `rummihub-deploy.service`, qui :

1. avance le dépôt sur `origin/master` (`git merge --ff-only`, avec la clé de déploiement en lecture seule) ;
2. lance `docker compose up -d --build --wait` : le build tourne pendant que l’ancienne version reste en ligne, seuls les services modifiés sont recréés, puis Compose attend les healthchecks ;
3. supprime les images devenues inutiles.

Le journal s’affiche dans l’onglet *Actions*. Un échec y passe au rouge et laisse l’ancienne version en ligne ; pour relancer : *Re-run jobs*, ou *Run workflow* sur `master`. Le déploiement tourne dans systemd : un job annulé ou une connexion coupée ne l’interrompt pas. Sur le VPS : `journalctl -u rummihub-deploy`.

Un déploiement qui modifie `webfrontend` le redémarre : 10 à 30 s de coupure, et les parties en cours sont perdues. `.github/` et `deploy/` sont exclus du contexte Docker : un merge qui ne touche qu’eux ne reconstruit pas les images.

### Installation (une fois, sur le VPS)

Depuis la racine du dépôt. Le chemin du dépôt et l’utilisateur `ubuntu` sont écrits dans le `.service`.

```bash
sudo install -m 644 deploy/autodeploy/rummihub-deploy.service /etc/systemd/system/
sudo install -m 755 deploy/autodeploy/rummihub-deploy /usr/local/bin/
echo 'ubuntu ALL=(root) NOPASSWD: /usr/bin/systemctl start rummihub-deploy.service' \
  | sudo tee /etc/sudoers.d/rummihub-deploy && sudo chmod 440 /etc/sudoers.d/rummihub-deploy
sudo systemctl daemon-reload
```

Ces deux fichiers ne suivent pas le dépôt : refaire les `install` après une modification.

Clé SSH de GitHub Actions, générée sur le VPS. Elle ne donne pas de shell : même divulguée, elle ne permet que de redéployer `master`.

```bash
ssh-keygen -t ed25519 -N '' -C github-actions-deploy -f /tmp/gha_deploy
echo "restrict,command=\"/usr/local/bin/rummihub-deploy\" $(cat /tmp/gha_deploy.pub)" >> ~/.ssh/authorized_keys
```

Secrets du dépôt (*Settings → Secrets and variables → Actions*, ou `gh secret set NOM -R AntoineRevel/RummiHub`) :

| Secret | Contenu |
|---|---|
| `DEPLOY_SSH_HOST` | IP publique du VPS |
| `DEPLOY_SSH_KEY` | `/tmp/gha_deploy` (clé privée) |
| `DEPLOY_SSH_KNOWN_HOSTS` | clés d’hôte lues sur le VPS : `for f in /etc/ssh/ssh_host_*_key.pub; do echo "<IP> $(cut -d' ' -f1,2 "$f")"; done` |

Puis `shred -u /tmp/gha_deploy /tmp/gha_deploy.pub`.

## Journaux

```bash
docker compose logs -f
docker compose logs -f webfrontend apiservice solver-worker
```

Les fichiers de log Docker sont bornés (10 Mo × 3).

## Vérifier que seuls 80 et 443 sont exposés

```bash
docker compose ps --format '{{.Service}} {{.Ports}}'
```

Seule la ligne `caddy` doit montrer `0.0.0.0:80->80` et `0.0.0.0:443->443`. Les autres services ont une colonne de ports vide. Depuis l’hôte :

```bash
curl -sv --max-time 3 http://127.0.0.1:6379 ; echo
curl -sv --max-time 3 http://127.0.0.1:8080/health ; echo
```

Les deux connexions doivent être refusées. `https://rummisolve.com/health` répond `Healthy` (sonde de vie, sans détail).

## Fichier `.env`

| Variable | Rôle |
|---|---|
| `SOLVER_API_KEY` | Secret partagé `SolverApi__Key` (≥ 32 caractères, hex). Identique des deux côtés, posé par Compose sur `webfrontend` et `apiservice`. |
| `DOMAIN` | Apex du certificat et du site. |
| `ACME_EMAIL` | Contact Let's Encrypt. |

Aucun mot de passe Redis : le port n’est pas publié. Les graines de rejeu durables sont `GameOptions:PredefinedIds` dans `RummiHub.Web/appsettings.json` (inclus dans l’image). Le bouton « sauver la graine » n’écrit pas le fichier en production.

## Essai local (sans DNS)

`rummisolve.localhost` se résout en 127.0.0.1. Certificat interne à Caddy (avertissement du navigateur, ou `curl -k`) :

```bash
cd deploy
cp .env.example .env   # SOLVER_API_KEY via openssl rand -hex 32
docker compose -f docker-compose.yml -f docker-compose.local.yml up -d --build
```

Ne pas utiliser cet override sur le VPS public : il remplace le Caddyfile Let's Encrypt.
