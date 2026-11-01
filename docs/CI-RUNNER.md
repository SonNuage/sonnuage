# Runner GitLab pour SonNuage

Le pipeline ([.gitlab-ci.yml](../.gitlab-ci.yml)) a besoin d'un runner **Docker** en mode **privileged**
(les jobs `ci-image`, `runtime-base` et `android-sdk-image` construisent des images avec docker-in-docker).
Sans runner rattaché au projet, les jobs restent « stuck ».

## 1. Créer le runner dans GitLab

Projet `sonnuage/sonnuage` → **Settings → CI/CD → Runners → New project runner** :

- *Tags* : aucun, et cocher **Run untagged jobs** (les jobs du pipeline n'ont pas de tags)
- *Description* : `homelab-docker`
- **Create runner**, puis copier le jeton `glrt-…` affiché (il n'est montré qu'une fois).

Pour le partager entre plusieurs projets : même chose dans le groupe ou dans **Admin → CI/CD → Runners**
(runner d'instance).

## 2. Lancer le runner sur l'hôte

Sur la machine Docker (par exemple celle qui héberge déjà GitLab), à côté du compose de GitLab :

```yaml
# compose.yaml
services:
  gitlab-runner:
    image: gitlab/gitlab-runner:v19.5.0   # garder la même version mineure que GitLab
    container_name: gitlab-runner
    restart: unless-stopped
    volumes:
      - ./runner-config:/etc/gitlab-runner
      - /var/run/docker.sock:/var/run/docker.sock
```

```bash
docker compose up -d gitlab-runner
```

```bash
docker exec -it gitlab-runner gitlab-runner register --non-interactive --url https://git.pmjb.org --token glrt-XXXXXXXX --executor docker --docker-image docker:28 --docker-privileged --docker-volumes /certs/client --description homelab-docker
```

Ensuite, dans `runner-config/config.toml` :

```toml
concurrent = 3                # en tête de fichier : tests, image et APK en parallèle

[[runners]]
  request_concurrency = 3
  [runners.docker]
    privileged = true
    # /nuget : paquets NuGet partagés par tous les jobs, sur un dossier de l'hôte (ni zip ni téléversement)
    volumes = ["/certs/client", "/cache", "/mnt/data/ci-cache/sonnuage/nuget:/nuget"]
    # Images publiques téléchargées une seule fois. Nos images latest (ci, android-sdk) demandent always.
    pull_policy = ["if-not-present"]
    allowed_pull_policies = ["always", "if-not-present"]
    shm_size = 268435456        # 256 Mo : le build Android (D8/R8) en profite
```

puis `docker restart gitlab-runner`. Le runner doit apparaître en vert dans Settings → CI/CD → Runners.
Relancer le pipeline bloqué (**Pipelines → Run pipeline** ou **Retry**).

Au premier pipeline, les images de build n'existent pas encore dans le registre : **Run pipeline** avec les
variables `BUILD_CI_IMAGE` = `1` et `BUILD_RUNTIME_BASE` = `1` (serveur) ou `BUILD_ANDROID_SDK` = `1` (Android).
Ensuite, elles ne sont reconstruites que quand leur recette change.

Sur homelab, le runner est dans `/home/selfhost/stacks/localify-runner` (conteneur `localify-gitlab-runner`).

## 3. Points d'attention

**Elasticsearch dans les tests.** Le job `server-test` démarre un Elasticsearch en service. Sur l'hôte du runner :

```bash
sudo sysctl -w vm.max_map_count=262144 && echo 'vm.max_map_count=262144' | sudo tee /etc/sysctl.d/99-elasticsearch.conf
```

**Mémoire.** Le build Android consomme 3 à 4 Go, les tests serveur (avec Elasticsearch) environ 2 Go. Avec
`concurrent = 3`, compter 8 Go libres. Réduire si la machine manque de mémoire.

**Images de commit.** Le job `server-smoke` démarre l'image de chaque commit sur l'hôte du runner. Les bases sont
partagées, chaque commit ajoute environ 70 Mo. De temps en temps :
`docker image prune -a --filter "until=168h"`.

**Cloudflare et le registre.** Si le registre (`$CI_REGISTRY`) passe par Cloudflare, chaque requête est limitée
à 100 Mo : la poussée d'une couche d'image plus grosse (l'étape `apt-get install ffmpeg` du serveur pèse ~150 Mo)
échoue en 413. Deux solutions :
- exposer le registre sur un nom d'hôte **non proxifié** (DNS seulement, nuage gris dans Cloudflare) ou
  joignable en LAN, et le déclarer comme `registry_external_url`.
- ou faire résoudre ce nom vers l'IP locale pour les jobs : dans `config.toml`,
  `extra_hosts = ["registry.example.org:192.168.1.10"]` (le certificat présenté en local doit alors être valide
  pour ce nom).

Le clonage et les artefacts (APK ~20 Mo) passent sans problème par Cloudflare.

**Sécurité.** `privileged = true` donne aux jobs un accès root à l'hôte : réserver ce runner aux projets de
confiance (runner de projet ou de groupe plutôt que d'instance si d'autres personnes utilisent le GitLab).

## 4. Signature de l'APK (facultatif)

```bash
keytool -genkeypair -v -keystore sonnuage.keystore -alias sonnuage -keyalg RSA -keysize 4096 -validity 10000
```

```bash
base64 -w0 sonnuage.keystore
```

Settings → CI/CD → Variables, toutes **Masked** et **Protected** :
`ANDROID_KEYSTORE_BASE64` (sortie de la commande ci-dessus), `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`
(`sonnuage`), `ANDROID_KEY_PASSWORD`. Garder le keystore en lieu sûr : sans lui, impossible de publier une mise à
jour installable par-dessus l'APK existant.
