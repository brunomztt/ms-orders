# Kitoca — Pedidos (serviço A)

C# / ASP.NET Core 10, MySQL exclusivo, REST com o Catálogo e eventos RabbitMQ.
O projeto mantém domínio, controller, repository e serviços de processamento.
Os conceitos REST seguem o [laboratório Back L01](https://devlabs.escdodev.com.br/labs/backend-api-rest).

## Executar

Docker Desktop com containers Linux. As pastas precisam estar lado a lado:

```text
KITOCA/
  ms-orders/
  ms-catalog-stock/
  ms-notifications-cloud/ms-notifications/
```

Dentro de `ms-orders`:

```powershell
docker compose up -d --build
docker compose ps
./scripts/test.ps1
```

O Compose integra A, B e C com três bancos exclusivos e um RabbitMQ compartilhado.
Não execute simultaneamente os Composes individuais nas mesmas portas.

| Serviço | Endereço |
| --- | --- |
| Pedidos | http://localhost:8081/api/orders |
| Catálogo / Swagger | http://localhost:8080/docs |
| Notificações / Swagger | http://localhost:8000/docs |
| RabbitMQ Management | http://localhost:15672 — kitoca / kitoca_local |

As credenciais do Compose são locais. Portas publicadas ficam em 127.0.0.1.
`docker compose stop` para; `docker compose start` retoma preservando os dados.

## API

| Método | Caminho | Resultado |
| --- | --- | --- |
| GET | /api/orders?offset=0&limit=50 | Lista, 200 |
| GET | /api/orders/{id} | Consulta, 200 ou 404 |
| POST | /api/orders | Cria rascunho, 201 + Location |
| PUT | /api/orders/{id} | Atualiza rascunho, 200 |
| DELETE | /api/orders/{id} | Exclui rascunho, 204 |
| POST | /api/orders/{id}/confirm | Envia para confirmação, 202 |
| GET | /api/orders/instance | Identifica Pod |
| GET | /health | Verifica MySQL, 200 ou 503 |
| GET | /health/live | Verifica API |

```json
{"userId":"tutor-01","items":[{"productId":1,"quantity":2}]}
```

Produtos devem existir no Catálogo. O preço vem de B. Dados inválidos retornam 400.
Use `src/MsOrders/MsOrders.http` para testar manualmente.

Estado: DRAFT → PENDING → CONFIRMED ou REJECTED. PUT/DELETE só aceitam DRAFT;
outros estados retornam 409. Confirmação repetida não reserva estoque novamente.
Falhas de rede mantêm PENDING e provocam nova tentativa com o mesmo pedido.

## Integração

Esta versão aceita **um produto por pedido**, com quantidade variável, e usa
as rotas originais de B: GET /api/catalog/products/{id} para obter o preço e
POST /api/catalog/products/{id}/reservations para reservar o estoque.
Não exige alterações nos projetos de Catálogo ou Notificações.

O MySQL armazena pedidos e eventos pendentes (outbox). A publica
`order.confirmed` ou `order.rejected` no exchange tópico durável
`app_events_exchange`, consumido por C na fila `pedidos_events`.
Falhas transitórias em B mantêm o pedido pendente; falhas no broker mantêm
o evento na outbox para nova tentativa.

C foi mantido na versão original. Não há garantia de deduplicação nem de
recuperação após falhas do MongoDB; a demonstração deve usar os serviços saudáveis.

## Testes

`./scripts/test.ps1` compila A e executa quatro testes integrados de CRUD,
validação do limite de um produto, confirmação com notificação, rejeição por falta
de estoque, concorrência e reserva idempotente. Usa Python dos containers.

## Kubernetes / EKS

`k8s/` contém o conjunto integrado: bancos com PVCs, RabbitMQ, Services ClusterIP,
três réplicas de A e Ingress NGINX sem reescrita. Pré-requisitos do cluster:
controller NGINX com classe nginx, driver EBS CSI e StorageClass gp3.

1. Publique as três imagens e ajuste registry/tags em `k8s/kustomization.yaml`.
2. Copie `runtime-secret.example.yaml` para fora do Git, troque as credenciais
   e mantenha as URLs consistentes.
3. Aplique namespace, Secret e conjunto:

```powershell
kubectl apply -f k8s/namespace.yaml
kubectl apply -f CAMINHO_SEGURO/runtime-secret.yaml
kubectl apply -k k8s
kubectl -n kitoca get pods,svc,pvc,ingress
kubectl -n kitoca rollout status deployment/ms-orders
```

`stack.json` é um manifesto Kubernetes válido. Não aplique a pasta inteira com
`-f`, pois inclui exemplos. `gp3.example.yaml` só é necessário se a classe ainda
não existir e o driver EBS já estiver configurado.

Na apresentação, demonstre o fluxo pedido → reserva REST → evento RabbitMQ →
notificação, os bancos separados e respostas de Pods diferentes em
`/api/orders/instance` pelo Ingress. O deploy no EKS continua necessário;
Compose local e validação de manifestos não comprovam funcionamento na AWS.

Cancelamento/devolução, pagamento real e alta disponibilidade dos bancos/broker
não fazem parte desta versão. As tabelas são criadas na inicialização.
