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

## Prérequis

- Ubuntu 24.04, Docker et Docker Compose v2 (le plugin `docker compose`).
- DNS : enregistrements A (et AAAA le cas échéant) de `rummisolve.com` **et** `www.rummisolve.com` vers le VPS, avant le premier `up` (Let's Encrypt).
- `ufw` : ouvrir seulement 22, 80 et 443. Ne pas publier 6379 ni le port de l’API.

## Premier déploiement

```bash
git clone https://github.com/AntoineRevel/RummiHub.git
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

```bash
cd RummiHub/deploy
git pull
docker compose up -d --build
```

Les volumes (certificats, AOF Redis, clés Data Protection) sont conservés. Un redémarrage de `webfrontend` efface les parties en cours ; les clés antiforgery, elles, restent sur le volume `dataprotection_keys`. Au démarrage, le journal peut signaler que la clé est stockée sans chiffrement : c’est le comportement par défaut de ASP.NET sur Linux, et le volume n’est pas publié.

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
