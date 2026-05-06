unset DOCKER_HOST
docker context use default
docker run -it --rm -p 8080:8080 -v $(pwd -W):/usr/local/structurizr structurizr/structurizr server