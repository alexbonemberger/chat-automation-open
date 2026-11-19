# Timestamp obrigatório nas mensagens (V7.0)

## Regra operacional

Toda mensagem **escrita ou enviada pelo ChatAutomation** começa com um cabeçalho de data e hora:

```text
[18/11/2026-00:32]
Conteúdo da mensagem
```

- Formato: `[dd/MM/yyyy-HH:mm]`, com relógio **UTC−03:00** fixo, calculado a partir de `DateTimeOffset.UtcNow`. Não depende da timezone do Windows.
- O cabeçalho ocupa **a primeira linha**, seguido de uma quebra de linha e do conteúdo original.
- `setText("mensagem")` escreve um rascunho já marcado, mas não o envia. `sendMessage()` reaproveita o mesmo cabeçalho, sem recalcular ou duplicá-lo. Assim, o horário representa quando o texto foi preparado.
- `sendMessage("mensagem")` prepara e tenta enviar com o cabeçalho do momento.
- Com `enableSendHumanText=true`, `sendMessage()` de um rascunho humano ainda sem timestamp acrescenta o cabeçalho e **relê/verifica** antes de clicar. Isso requer substituir o rascunho no editor para inserir a primeira linha. Se não houver prova de readback, o envio é bloqueado.
- Com um cabeçalho válido já na primeira linha, o aplicativo preserva o texto e não adiciona um segundo. Isso vale em chamadas repetidas e nos caminhos CLI legados.
- `clear()`, `getText()`, `isBusy()`, eventos JSON do console e URLs **não** recebem prefixo.
- Os status de envio continuam `INPUT_DISPATCHED_UNCONFIRMED`: comprovação de clique não é comprovação de recebimento remoto.

## Exemplos

```text
use "chat00"
setText("Teste operacional")
getText()
sendMessage()
```

A chamada `getText()` deve ler algo como:

```text
[18/11/2026-00:32]
Teste operacional
```

**O exemplo é ilustrativo; o horário é calculado a cada nova preparação de mensagem.** O primeiro teste pode ser feito apenas com `setText()` e `getText()`, sem enviar nada. Limpe com `clear()` se desejar.

## Rotas cobertas

`Chat.SetText`, `Chat.SendMessage(string)`, `Chat.SendMessage()`, `ChatFeatures.SetText`, `ChatFeatures.SendMessage` (CLI legado) e envio de rascunho humano por `ChatFeatures.SendCurrentEditor`.

A normalização idempotente está centralizada em `ChatAutomation.Domain.MessageTimestamp`; não adicionamos datas no parser nem nas mensagens de resultado do console.

## Testes

O `self-test` verifica:
- Conversão UTC → UTC−3, inclusive mudança de dia.
- Igualdade de instantes representados em fusos diferentes.
- Preservação de conteúdo multilinha.
- Ausência de cabeçalho em editor vazio.
- Prevenção de prefixo duplicado, inclusive após decurso do tempo.
- Rejeição de datas inválidas em cabeçalhos preexistentes.

A prova de escrita real no Edge requer o Windows e uma conversa que ainda aceite mensagens. `GPTBOT_0001` já está com `isAtMaxLimit=true`, portanto não deve ser usado em teste de envio.
