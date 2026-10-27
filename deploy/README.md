# Déployer RummiHub (un VPS, Docker Compose)

Site public : `https://rummisolve.com`. `https://www.rummisolve.com` redirige vers l’apex. Le site de test `https://test.rummisolve.com` est une seconde pile, sans Caddy ni port publié, arrêtée deux heures après chaque déploiement ; voir « Site de test ».

Les processus de l’AppHost Aspire, tableau de bord compris (sans Redis Insight) :

| Service | Rôle | Exposition |
|---|---|---|
| `caddy` | TLS Let's Encrypt, HTTP→HTTPS, WebSocket / SignalR | **80 et 443 seulement** |
| `webfrontend` | Blazor Server, une seule réplique | réseau `internal` |
| `apiservice` | `POST /solve` | réseau `internal` |
| `solver-worker` | 2 répliques, groupe Redis `solvers` | réseau `internal` |
| `cache` | Redis 8.10.2 (RedisJSON inclus), volume persistant | réseau `internal` |
| `aspire-dashboard` | traces, métriques et journaux structurés (OTLP), en mémoire | `127.0.0.1:18888`, par tunnel SSH |

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

Coller la valeur dans `SOLVER_API_KEY` (64 caractères hex, sans `$` ni espace), puis une autre dans `OTLP_API_KEY`. Renseigner `DOMAIN` (`rummisolve.com`) et `ACME_EMAIL`. Puis :

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

Les volumes (certificats, AOF Redis, clés Data Protection) sont conservés. Le volume `redis_data` reste monté sur `/data`. Redis 8.10 relit l’AOF écrit par Redis Stack 7.4 (`appendonlydir`, commandes JSON, streams, chaînes). Au premier démarrage le conteneur donne `/data` à l’utilisateur `redis` : Stack écrivait en root, et les répertoires vides `/data/redis` et `/data/redisinsight` empêchent l’entrypoint officiel de corriger les droits lui-même. RedisGears et RedisCompat, chargés par Stack, ne sont pas dans l’image officielle ; le pipeline n’en a pas besoin, et un volume sans clés Gears démarre. Le retour vers l’image Stack n’est pas pris en charge : dès que Redis 8 réécrit la base AOF, le fichier est en RDB version 15, que Redis 7.4 refuse. Si Redis refusait malgré tout de charger le volume, le vider ne perd que le cache de solves (TTL 1 h) et les messages de stream en cours — les parties sont dans `webfrontend`, pas dans Redis. Un redémarrage de `webfrontend` efface les parties en cours ; les clés antiforgery, elles, restent sur le volume `dataprotection_keys`. Au démarrage, le journal peut signaler que la clé est stockée sans chiffrement : c’est le comportement par défaut de ASP.NET sur Linux, et le volume n’est pas publié.

## Déploiement automatique

Deux workflows. `.github/workflows/pr.yml` lance les tests sur chaque PR, puis met le commit testé sur le site de test (section « Site de test »). La protection de `master` exige le check `test` et une branche à jour : le code qui arrive sur `master` a déjà passé les tests sur la PR. `.github/workflows/deploy.yml` déploie donc la production directement après le merge (push sur `master`, ou *Run workflow*), sans les relancer. Il se connecte en SSH au VPS **sans commande distante**. La clé ne peut exécuter que `rummihub-deploy` : sans commande, cela démarre `rummihub-deploy.service`, qui :

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
sudo install -m 644 deploy/autodeploy/rummihub-test-autostop.service \
  deploy/autodeploy/rummihub-test-autostop.timer /etc/systemd/system/
sudo install -m 644 deploy/autodeploy/rummihub.tmpfiles /etc/tmpfiles.d/rummihub.conf
sudo install -m 755 deploy/autodeploy/rummihub-deploy \
  deploy/autodeploy/rummihub-deploy-test \
  deploy/autodeploy/rummihub-deploy-test-run \
  deploy/autodeploy/rummihub-ensure-edge-network \
  deploy/autodeploy/rummihub-compose-prod \
  deploy/autodeploy/rummihub-test-autostop \
  /usr/local/bin/
echo 'ubuntu ALL=(root) NOPASSWD: /usr/bin/systemctl start rummihub-deploy.service' \
  | sudo tee /etc/sudoers.d/rummihub-deploy && sudo chmod 440 /etc/sudoers.d/rummihub-deploy
echo 'ubuntu ALL=(root) NOPASSWD: /usr/bin/systemctl start rummihub-deploy-test@*.service' \
  | sudo tee /etc/sudoers.d/rummihub-deploy-test && sudo chmod 440 /etc/sudoers.d/rummihub-deploy-test
sudo systemd-tmpfiles --create /etc/tmpfiles.d/rummihub.conf
sudo systemctl daemon-reload
sudo systemctl enable --now rummihub-test-autostop.timer
```

Ces fichiers ne suivent pas le dépôt une fois copiés : refaire les `install` après une modification. Le déploiement de prod le signale : juste après la mise à jour du dépôt, `rummihub-check-installed` (lancé depuis le dépôt, jamais installé) compare chaque copie installée à `deploy/autodeploy/`. Une différence apparaît en avertissement jaune dans le résumé du run *Actions*, avec les commandes `sudo install` à coller sur le VPS. Le déploiement continue. Les sudoers ne sont pas vérifiés. À la main : `deploy/autodeploy/rummihub-check-installed`.

Clé SSH de GitHub Actions, générée sur le VPS. Elle ne donne pas de shell. Sans commande distante, elle ne fait que redéployer `master`. La seule autre commande acceptée est `deploy-test <SHA>` (commit complet, 40 caractères hexadécimaux), qui met à jour le site de test (section suivante) et ne touche pas à la pile de production. Même clé, mêmes secrets : pas de `DEPLOY_TEST_SSH_KEY`. La ligne `authorized_keys` ne change pas.

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
| Durée de vie | permanente | arrêtée 2 h après le dernier déploiement |
| Tableau de bord Aspire | oui | non (rien n’est exporté) |

Seul lien : le réseau Docker **externe et interne** `rummihub-edge`. Le Caddy de production y est attaché et proxifie `test.<DOMAIN>` vers l’alias `webfrontend-test`. Le réseau n’a pas de passerelle : la pile de test ne s’en sert pas pour sortir, ni pour atteindre Redis ou l’API de production (autres réseaux). Si le conteneur de test est arrêté (arrêt automatique compris) ou en cours de recréation, ce nom répond `502` avec « Site de test indisponible. » Le site public continue. L’en-tête `X-Robots-Tag: noindex, nofollow` est posé sur les réponses, y compris ce 502.

Le Caddy de production vise le site public par le nom du conteneur, `rummihub-webfrontend-1`, et non par `webfrontend` : sur `rummihub-edge`, Docker donne aussi le nom de service `webfrontend` au webfrontend de test, et Caddy envoyait alors tout le site public vers la pile de test. Ne jamais viser un nom de service depuis Caddy pour la production.

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
sudo install -m 644 deploy/autodeploy/rummihub-test-autostop.service \
  deploy/autodeploy/rummihub-test-autostop.timer /etc/systemd/system/
sudo install -m 644 deploy/autodeploy/rummihub.tmpfiles /etc/tmpfiles.d/rummihub.conf
sudo install -m 755 deploy/autodeploy/rummihub-deploy \
  deploy/autodeploy/rummihub-deploy-test \
  deploy/autodeploy/rummihub-deploy-test-run \
  deploy/autodeploy/rummihub-ensure-edge-network \
  deploy/autodeploy/rummihub-compose-prod \
  deploy/autodeploy/rummihub-test-autostop \
  /usr/local/bin/
echo 'ubuntu ALL=(root) NOPASSWD: /usr/bin/systemctl start rummihub-deploy-test@*.service' \
  | sudo tee /etc/sudoers.d/rummihub-deploy-test && sudo chmod 440 /etc/sudoers.d/rummihub-deploy-test
sudo systemd-tmpfiles --create /etc/tmpfiles.d/rummihub.conf
sudo systemctl daemon-reload
sudo systemctl enable --now rummihub-test-autostop.timer
```

La ligne `authorized_keys` (`restrict,command="/usr/local/bin/rummihub-deploy"`) et les secrets `DEPLOY_SSH_HOST`, `DEPLOY_SSH_KEY`, `DEPLOY_SSH_KNOWN_HOSTS` restent ceux de la production. Le sudoers de prod (`systemctl start rummihub-deploy.service`) aussi.

Puis le clone de test, avec la même clé de déploiement en lecture seule (`github-rummihub`) :

```bash
git clone git@github-rummihub:AntoineRevel/RummiHub.git /home/ubuntu/apps/RummiHub-test
cd /home/ubuntu/apps/RummiHub-test/deploy
cp .env.test.example .env.test
openssl rand -hex 32
```

Coller la valeur dans `SOLVER_API_KEY` de `.env.test`. Ce n’est pas la clé de la production. `DOMAIN`, `ACME_EMAIL` et `OTLP_API_KEY` (valeur quelconque) sont là pour l’interpolation du compose de base ; ni le Caddy ni le tableau de bord ne démarrent sur la pile de test. Ne pas lancer `docker compose up` sans `-f docker-compose.test.yml` dans ce clone : le fichier de base publierait 80 et 443.

Le clone de test est jetable (`git checkout --force` et `git clean -ffd` à chaque déploiement). `.env` et `.env.test` sont gitignorés, donc gardés par le `clean` ; le reste des fichiers non suivis est supprimé.

Enfin, une fois le réseau créé et les enregistrements DNS en place, le redéploiement prod unique :

```bash
rummihub-deploy
```

(`sudo systemctl start rummihub-deploy.service` si les scripts ne sont pas encore ceux décrits plus haut, et que le réseau existe déjà.) Vérifier que seul Caddy a été recréé : `docker compose ps` dans `deploy/`, colonne `CREATED`.

### Lancer un déploiement

Automatique : chaque push sur une PR lance `.github/workflows/pr.yml`, qui fait `dotnet test` puis, si les tests passent, le SSH `deploy-test <SHA>` avec la pointe de la branche (pas le commit de merge temporaire de la PR). Le site montre ce commit. Il n’y a pas de branche `test`. Une PR venue d’un fork n’est pas déployée (pas de secrets).

Il n’y a qu’une pile de test : elle montre **la dernière PR poussée**. Un déploiement à la fois, jamais annulé au milieu ; s’il en arrive plusieurs pendant ce temps, seul le plus récent passe ensuite. Pour remettre une PR sur le site, ou le relancer après l’arrêt automatique : dans son run *Actions*, relancer le seul job `test-site` (*Re-run* sur le job), les tests ne sont pas refaits.

Pour une branche sans PR : *Actions → Tests and test site → Run workflow*, branche choisie dans *Use workflow from* (une étiquette est refusée), ou :

```bash
gh workflow run pr.yml -R AntoineRevel/RummiHub --ref ma-branche
```

La branche doit contenir `.github/workflows/pr.yml` (sinon merger `master` dedans).

À la main, sur le VPS : `rummihub-deploy-test <SHA complet>`, par exemple `rummihub-deploy-test "$(git ls-remote origin refs/heads/ma-branche | cut -f1)"` depuis un clone. Il démarre `rummihub-deploy-test@<SHA>.service`, qui fait `git fetch origin master <SHA>`, `git checkout --force --detach <SHA>`, puis `docker compose -p rummihub-test … up -d --build --wait --remove-orphans`. Journal : l’onglet *Actions*, ou `journalctl -u 'rummihub-deploy-test@*'`.

Un bandeau rose en bas à gauche du site de test indique ce qui tourne : « Site de test · branche · commit ». `rummihub-deploy-test-run` le passe au webfrontend (`TestSite__Commit`) ; la branche est celle dont le commit est la pointe sur `origin`, absente si elle a avancé depuis. La production n'a pas cette variable, donc pas de bandeau.

**`deploy/` doit être celui de `master`.** Si le commit modifie `deploy/` depuis son point de départ sur `master` (`git diff origin/master...<SHA> -- deploy`), le déploiement est refusé avant le checkout. La pile de test tourne avec les fichiers Compose du commit : un Compose modifié (volume `/`, `docker.sock`, `privileged`…) donnerait root sur le VPS de production. Le code applicatif et le `Dockerfile` peuvent changer librement. Le workflow fait la même vérification avant le SSH : une PR qui touche `deploy/` reste verte, avec une note « not deployed to the test site ». Pour essayer une modification de `deploy/`, la merger d’abord.

### Arrêter le site de test

La pile de test s’arrête seule **deux heures après son dernier déploiement**, pour rendre le CPU et la mémoire à la production. `rummihub-test-autostop.timer` lance `rummihub-test-autostop` toutes les 5 minutes. Chaque déploiement de test touche `/run/rummihub/test-deployed` juste avant `docker compose up` ; si ce fichier a plus de 2 h, ou s’il manque, le script fait `docker compose … stop` sur `rummihub-test`. L’arrêt tombe donc entre 2 h et 2 h 05. Pendant un déploiement (prod ou test), le verrou `compose.lock` est pris : le script passe son tour.

- Relancer pour deux heures : relancer le job `test-site` du dernier run de la PR (ou *Run workflow* sur la branche). `up` redémarre les conteneurs arrêtés ; avec le cache Docker, le build d’un commit déjà déployé est court.
- Prolonger une pile qui tourne : `touch /run/rummihub/test-deployed` sur le VPS.
- Au boot, `/run` est vide. Une pile de test qui tournait repart (`restart: unless-stopped`) et s’arrête au passage suivant du timer ; une pile arrêtée reste arrêtée.
- À l’installation du timer, le fichier n’existe pas encore : la pile en cours s’arrête dans les 5 minutes. `touch /run/rummihub/test-deployed` avant `enable --now` lui laisse deux heures.
- Journal : `journalctl -u rummihub-test-autostop`.

À la main, sans toucher à la production :

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

Les fichiers de log Docker sont bornés (10 Mo × 3) et disparaissent quand Compose recrée le conteneur, donc à chaque déploiement qui touche le service.

### Traces et métriques

`webfrontend`, `apiservice` et les deux `solver-worker` envoient leurs traces, métriques et journaux structurés au tableau de bord Aspire (`aspire-dashboard`, OTLP gRPC sur le port 18889 du réseau `internal`). Il n’accepte que les envois qui portent la clé `OTLP_API_KEY` de `.env` (en-tête `x-otlp-api-key`, posé par Compose sur les trois services). C’est le même tableau de bord que celui d’`aspire run`, sans la page des ressources ni les journaux console. Tout reste en mémoire : un redémarrage du conteneur efface l’historique. Il garde les 10 000 dernières traces et entrées de journal par service.

L’interface n’écoute que sur `127.0.0.1:18888` du VPS. Depuis sa machine :

```bash
ssh -N -L 18888:127.0.0.1:18888 ubuntu@54.37.139.20
```

Puis ouvrir l’adresse de connexion, qui contient un jeton tiré à chaque démarrage du conteneur :

```bash
docker logs rummihub-aspire-dashboard-1 2>&1 | grep -m1 'login?t='
```

Le navigateur garde la session ensuite.

La même télémétrie se lit en JSON depuis le VPS, avec la clé `DASHBOARD_API_KEY` de `.env` (sans clé, l’API répond 401) :

```bash
K="x-api-key: $(grep '^DASHBOARD_API_KEY=' ~/apps/RummiHub/deploy/.env | cut -d= -f2)"
curl -s -H "$K" http://127.0.0.1:18888/api/telemetry/resources
curl -s -H "$K" 'http://127.0.0.1:18888/api/telemetry/traces?resource=webfrontend&limit=20'
curl -s -H "$K" http://127.0.0.1:18888/api/telemetry/traces/<traceId>
curl -s -H "$K" 'http://127.0.0.1:18888/api/telemetry/logs?resource=apiservice&severity=Warning'
```

Les métriques ne sont que dans l’interface. Aucun service ne dépend du tableau de bord : arrêté, il ne bloque rien, et les traces de ce moment-là sont perdues. Le site de test n’a pas de tableau de bord ; ses services n’exportent rien.

## Vérifier que seuls 80 et 443 sont exposés

```bash
docker compose ps --format '{{.Service}} {{.Ports}}'
```

Seule la ligne `caddy` doit montrer `0.0.0.0:80->80` et `0.0.0.0:443->443`. `aspire-dashboard` montre `127.0.0.1:18888->18888`, joignable depuis le VPS seulement. Les autres services ont une colonne de ports vide. Depuis l’hôte :

```bash
curl -sv --max-time 3 http://127.0.0.1:6379 ; echo
curl -sv --max-time 3 http://127.0.0.1:8080/health ; echo
```

Les deux connexions doivent être refusées. Depuis une autre machine, `curl --max-time 3 http://54.37.139.20:18888` doit aussi échouer. `https://rummisolve.com/health` répond `Healthy` (sonde de vie, sans détail).

## Fichier `.env`

| Variable | Rôle |
|---|---|
| `SOLVER_API_KEY` | Secret partagé `SolverApi__Key` (≥ 32 caractères, hex). Identique des deux côtés, posé par Compose sur `webfrontend` et `apiservice`. |
| `DOMAIN` | Apex du certificat et du site. |
| `ACME_EMAIL` | Contact Let's Encrypt. |
| `DASHBOARD_API_KEY` | Facultative. Clé de l’API de télémétrie du tableau de bord (`openssl rand -hex 32`). Vide : API fermée. |
| `OTLP_API_KEY` | Obligatoire. Clé que le tableau de bord exige pour recevoir la télémétrie (`openssl rand -hex 32`). Compose la pose sur le tableau de bord et, en en-tête `x-otlp-api-key`, sur `webfrontend`, `apiservice` et `solver-worker`. Absente : Compose refuse de démarrer, rien n’est recréé. |
| `SEED_CATALOG_KEY` | Obligatoire, **identique** dans `deploy/.env` et `deploy/.env.test` (`openssl rand -hex 32`). En-tête `X-Seed-Catalog-Key` du catalogue de graines. Absente : Compose refuse de démarrer, rien n’est recréé. |

Aucun mot de passe Redis : le port n’est pas publié.

## Graines de distribution

Une graine fait 64 caractères hexadécimaux (32 octets, 256 bits). Le même texte distribue toujours les mêmes tuiles. Le code de partie, 8 caractères, ne sert qu’à rejoindre un salon. Trois anciennes graines de 8 caractères (`GameOptions:PredefinedIds` dans `appsettings.json`) sont écrites une seule fois dans un catalogue vide ; ensuite le fichier est la seule liste.

La liste est un fichier JSONL, une ligne par ajout, **sans opération de suppression**. Elle vit dans le volume Docker `seed_catalog`, monté seulement sur le `webfrontend` de **production** (`/var/rummihub/seeds/catalog.jsonl`). Au premier démarrage le volume reçoit le répertoire vide de l’image, appartenant à l’utilisateur `app`. Ne pas créer ce volume à la main avant, et ne pas le supprimer (`docker volume rm`) : un `docker compose down` sans `-v` le conserve, un redéploiement aussi.

Le site de test ne monte pas ce volume. Son `webfrontend` appelle `http://seed-catalog:8080/internal/seed-catalog` (alias du webfrontend de prod sur `rummihub-edge`) avec la clé partagée. Deux routes : lire la liste, ajouter une graine valide. Pas de PUT, pas de DELETE. Un bug ou un redéploiement du site de test ne peut donc pas vider le fichier. Au pire, il y ajoute des graines de 64 caractères hex (plafond 500, au plus 60 ajouts par minute). Caddy répond 404 sur `/internal/*` du site public : la route n’est pas exposée sur Internet.

`webfrontend` de prod rejoint `rummihub-edge` pour cet alias. Ne pas faire résoudre le nom `webfrontend` sur ce réseau : la pile de test y porte le même nom de service. Caddy continue de viser `rummihub-webfrontend-1` et `webfrontend-test`.

Avant le premier déploiement de ce changement, poser la même `SEED_CATALOG_KEY` dans les deux fichiers d’environnement. Sinon Compose s’arrête avant de recréer les conteneurs. Le déploiement recrée `webfrontend` (les parties en mémoire sont perdues) et Caddy (le `Caddyfile` change : quelques secondes sans HTTPS).

Le job de site de test refuse un commit qui modifie `deploy/`. Une fois le commit sur `master`, mettre le site de test à jour à la main : `rummihub-deploy-test <SHA complet de master>` (le contrôle `deploy/` passe, car le commit est `master`).

## Essai local (sans DNS)

`rummisolve.localhost` se résout en 127.0.0.1. Certificat interne à Caddy (avertissement du navigateur, ou `curl -k`) :

```bash
cd deploy
cp .env.example .env   # SOLVER_API_KEY et OTLP_API_KEY via openssl rand -hex 32
docker compose -f docker-compose.yml -f docker-compose.local.yml up -d --build
```

Ne pas utiliser cet override sur le VPS public : il remplace le Caddyfile Let's Encrypt.
