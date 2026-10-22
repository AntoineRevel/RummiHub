# Déployer RummiHub (un VPS, Docker Compose)

Site public : `https://rummisolve.com`. `https://www.rummisolve.com` redirige vers l’apex. Le site de test `https://test.rummisolve.com` est une seconde pile, sans Caddy ni port publié ; voir « Site de test ».

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

Le workflow `.github/workflows/deploy.yml` lance les tests sur chaque PR. La protection de `master` exige le check `test` et une branche à jour : le code qui arrive sur `master` a déjà passé les tests sur la PR. Le déploiement part donc directement après le merge (push sur `master`, ou *Run workflow*), sans les relancer. Il se connecte en SSH au VPS **sans commande distante**. La clé ne peut exécuter que `rummihub-deploy` : sans commande, cela démarre `rummihub-deploy.service`, qui :

1. avance le dépôt sur `origin/master` (`git merge --ff-only`, avec la clé de déploiement en lecture seule) ;
2. lance `rummihub-compose-prod` : crée le réseau `rummihub-edge` s’il manque, attend le verrou partagé avec le site de test, puis `docker compose up -d --build --wait`. Le build tourne pendant que l’ancienne version reste en ligne, seuls les services modifiés sont recréés, puis Compose attend les healthchecks. Si le verrou est déjà pris, le script échoue **sans** lancer Compose : l’ancienne pile reste en place ;
3. supprime les images devenues inutiles (images sans tag, pas les tags `:local` ni `:test`).

Le journal s’affiche dans l’onglet *Actions*. Un échec y passe au rouge et laisse l’ancienne version en ligne ; pour relancer : *Re-run jobs*, ou *Run workflow* sur `master`. Le déploiement tourne dans systemd : un job annulé ou une connexion coupée ne l’interrompt pas. Sur le VPS : `journalctl -u rummihub-deploy`.

Un déploiement qui modifie `webfrontend` le redémarre : 10 à 30 s de coupure, et les parties en cours sont perdues. `.github/` et `deploy/` sont exclus du contexte Docker : un merge qui ne touche qu’eux ne reconstruit pas les images.

### Installation (une fois, sur le VPS)

Depuis la racine du dépôt. Le chemin du dépôt et l’utilisateur `ubuntu` sont écrits dans le `.service`.

```bash
sudo install -m 644 deploy/autodeploy/rummihub-deploy.service /etc/systemd/system/
sudo install -m 644 deploy/autodeploy/rummihub-deploy-test@.service /etc/systemd/system/
sudo install -m 644 deploy/autodeploy/rummihub.tmpfiles /etc/tmpfiles.d/rummihub.conf
sudo install -m 755 deploy/autodeploy/rummihub-deploy \
  deploy/autodeploy/rummihub-deploy-test \
  deploy/autodeploy/rummihub-deploy-test-run \
  deploy/autodeploy/rummihub-branch-ok \
  deploy/autodeploy/rummihub-ensure-edge-network \
  deploy/autodeploy/rummihub-compose-prod \
  /usr/local/bin/
echo 'ubuntu ALL=(root) NOPASSWD: /usr/bin/systemctl start rummihub-deploy.service' \
  | sudo tee /etc/sudoers.d/rummihub-deploy && sudo chmod 440 /etc/sudoers.d/rummihub-deploy
sudo install -m 440 deploy/autodeploy/sudoers-rummihub-deploy-test /etc/sudoers.d/rummihub-deploy-test
sudo visudo -cf /etc/sudoers.d/rummihub-deploy-test
sudo systemd-tmpfiles --create /etc/tmpfiles.d/rummihub.conf
sudo systemctl daemon-reload
```

Ces fichiers ne suivent pas le dépôt une fois copiés : refaire les `install` après une modification.

Clé SSH de GitHub Actions, générée sur le VPS. Elle ne donne pas de shell. Sans commande distante, elle ne fait que redéployer `master`. La seule autre commande acceptée est `deploy-test <branche>`, qui met à jour le site de test (section suivante) et ne touche pas à la pile de production. Même clé, mêmes secrets : pas de `DEPLOY_TEST_SSH_KEY`. La ligne `authorized_keys` ne change pas.

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

## Site de test

`https://test.rummisolve.com` tourne à côté de la production, sur le même VPS, sans partager ses conteneurs, son Redis, ni ses parties.

| | Production | Test |
|---|---|---|
| Clone | `/home/ubuntu/apps/RummiHub` | `/home/ubuntu/apps/RummiHub-test` |
| Projet Compose | `rummihub` | `rummihub-test` |
| Fichiers | `docker-compose.yml` | `docker-compose.yml` + `docker-compose.test.yml` |
| Caddy, ports 80/443 | oui | non |
| Workers | 2 × 1,5 CPU, 1536 Mio | 1 × 1,0 CPU, 1024 Mio (`cpu_shares` 512) |
| Mémoire plafonnée | voir le compose | web 512 Mio, API 256 Mio, Redis 320 Mio (maxmemory 192 Mio), sans swap au-delà |
| Images | `:local` | `:test` |
| Secret | `deploy/.env` | `deploy/.env.test` (autre `SOLVER_API_KEY`) |
| Réseau applicatif | `internal`, sans Internet | le sien, sans Internet |

Seul lien : le réseau Docker **externe et interne** `rummihub-edge`. Le Caddy de production y est attaché et proxifie `test.<DOMAIN>` vers l’alias `webfrontend-test`. Le réseau n’a pas de passerelle : la pile de test ne s’en sert pas pour sortir, ni pour atteindre Redis ou l’API de production (autres réseaux). Si le conteneur de test est arrêté ou en cours de recréation, ce nom répond `502` avec « Site de test indisponible. » Le site public continue. L’en-tête `X-Robots-Tag: noindex, nofollow` est posé sur les réponses, y compris ce 502.

Un déploiement de test ne recrée aucun conteneur `rummihub`. Il ne recharge pas Caddy : le nom `webfrontend-test` est résolu toutes les 5 secondes. Le build est en revanche sérialisé avec celui de la prod (verrou `/run/rummihub/compose.lock`), pour ne pas lancer deux publications .NET en même temps sur 8 Go de RAM.

### Ce qui demande un redéploiement de la production

Une seule fois, pour que Caddy joigne `rummihub-edge` et charge la route `test.<DOMAIN>`. `docker compose up` ne recrée que les services dont la définition a changé : ici, **caddy**. `webfrontend`, `apiservice`, les workers et Redis gardent leur conteneur (les parties en mémoire aussi), tant que l’image n’a pas changé. `deploy/` est dans `.dockerignore` : ce changement ne reconstruit pas les images applicatives.

C’est un redémarrage de Caddy, quelques secondes sans HTTP/HTTPS. Un `caddy reload` ne suffit pas : le conteneur doit être recréé pour entrer dans le réseau. Les déploiements de test suivants ne refont pas cette opération.

Si le réseau n’existe pas encore, `docker compose up` s’arrête tout de suite avec « network rummihub-edge declared as external, but could not be found », **sans recréer** les conteneurs. La prod reste donc en ligne ; on crée le réseau et on relance.

### DNS

Avant ce redéploiement, pour que Let’s Encrypt délivre le certificat au premier coup (Caddy réessaie sinon, le site public garde son certificat déjà émis) :

| Type | Nom | Valeur |
|---|---|---|
| A | `test.rummisolve.com` | `54.37.139.20` |
| AAAA | `test.rummisolve.com` | `2001:41d0:601:1100::99c` |

### Installation (une fois, sur le VPS)

À faire **avant le premier lancement de l’action de test**. Tant que `/usr/local/bin/rummihub-deploy` est encore l’ancien script, il ignore la commande distante et redéploie `master` sur la **production**.

Depuis la racine du clone de production (après le merge, ou en prenant les fichiers de la branche) :

```bash
cd /home/ubuntu/apps/RummiHub

docker network inspect rummihub-edge >/dev/null 2>&1 \
  || docker network create --internal rummihub-edge

sudo install -m 644 deploy/autodeploy/rummihub-deploy.service /etc/systemd/system/
sudo install -m 644 deploy/autodeploy/rummihub-deploy-test@.service /etc/systemd/system/
sudo install -m 644 deploy/autodeploy/rummihub.tmpfiles /etc/tmpfiles.d/rummihub.conf
sudo install -m 755 deploy/autodeploy/rummihub-deploy \
  deploy/autodeploy/rummihub-deploy-test \
  deploy/autodeploy/rummihub-deploy-test-run \
  deploy/autodeploy/rummihub-branch-ok \
  deploy/autodeploy/rummihub-ensure-edge-network \
  deploy/autodeploy/rummihub-compose-prod \
  /usr/local/bin/
sudo install -m 440 deploy/autodeploy/sudoers-rummihub-deploy-test /etc/sudoers.d/rummihub-deploy-test
sudo visudo -cf /etc/sudoers.d/rummihub-deploy-test
sudo systemd-tmpfiles --create /etc/tmpfiles.d/rummihub.conf
sudo systemctl daemon-reload
```

La ligne `authorized_keys` (`restrict,command="/usr/local/bin/rummihub-deploy"`) et les secrets `DEPLOY_SSH_HOST`, `DEPLOY_SSH_KEY`, `DEPLOY_SSH_KNOWN_HOSTS` restent ceux de la production. Le sudoers de prod (`systemctl start rummihub-deploy.service`) aussi.

Puis le clone de test, avec la même clé de déploiement en lecture seule (`github-rummihub`) :

```bash
git clone git@github-rummihub:AntoineRevel/RummiHub.git /home/ubuntu/apps/RummiHub-test
cd /home/ubuntu/apps/RummiHub-test/deploy
cp .env.test.example .env.test
openssl rand -hex 32
```

Coller la valeur dans `SOLVER_API_KEY` de `.env.test`. Ce n’est pas la clé de la production. `DOMAIN` et `ACME_EMAIL` sont là pour l’interpolation du compose de base ; le Caddy de test ne démarre pas. Ne pas lancer `docker compose up` sans `-f docker-compose.test.yml` dans ce clone : le fichier de base publierait 80 et 443.

Le clone de test est jetable (`git checkout --force` et `git clean -ffd` à chaque déploiement). `.env` et `.env.test` sont gitignorés et exclus du `clean` ; le reste des fichiers non suivis est supprimé.

Enfin, une fois le réseau créé et les enregistrements DNS en place, le redéploiement prod unique :

```bash
rummihub-deploy
```

(`sudo systemctl start rummihub-deploy.service` si les scripts ne sont pas encore ceux décrits plus haut, et que le réseau existe déjà.) Vérifier que seul Caddy a été recréé : `docker compose ps` dans `deploy/`, colonne `CREATED`.

### Lancer un déploiement

*Actions → Déploiement du site de test → Run workflow*, puis choisir la branche dans *Use workflow from*. Il n’y a pas d’autre champ : cette branche (`github.ref_name`) est celle qui est testée, puis envoyée au VPS. Elle doit contenir `.github/workflows/deploy-test.yml` — c’est le cas de toute branche créée depuis `master` après la PR #39. Une étiquette est refusée. Les tests (`dotnet test`) tournent d’abord ; le SSH n’a lieu qu’ensuite. Un déploiement à la fois, jamais annulé au milieu.

```bash
gh workflow run deploy-test.yml -R AntoineRevel/RummiHub --ref ma-branche
```

À la main, sur le VPS : `rummihub-deploy-test ma-branche`. Le nom doit matcher `^[A-Za-z0-9._/-]{1,100}$`, sans `..` et sans `-` initial, et exister sur `origin`. Sinon le script s’arrête avant `systemctl`. Il transmet la branche au service comme nom d’instance hexadécimal (`rummihub-deploy-test@<hex>.service`) : un `/` n’est pas perdu, et deux lancements ne s’écrasent pas la branche. Le verrou Docker les fait ensuite s’enchaîner.

Le service fait `git fetch origin <branche>`, `git checkout --force --detach FETCH_HEAD`, puis `docker compose -p rummihub-test … up -d --build --wait --remove-orphans`. Journal : l’onglet *Actions*, ou `journalctl -u 'rummihub-deploy-test@*'`.

Arrêter le site de test sans toucher à la production :

```bash
cd /home/ubuntu/apps/RummiHub-test/deploy
docker compose -p rummihub-test --env-file .env.test \
  -f docker-compose.yml -f docker-compose.test.yml stop
```

`down` retire les conteneurs de test mais pas le réseau externe `rummihub-edge`. Caddy répond alors 502 sur `test.rummisolve.com`.

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
