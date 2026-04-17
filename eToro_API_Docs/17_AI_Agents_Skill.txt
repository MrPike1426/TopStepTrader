> ## Documentation Index
> Fetch the complete documentation index at: https://api-portal.etoro.com/llms.txt
> Use this file to discover all available pages before exploring further.

# SKILL.md

> Agent Skill for accessing the eToro API

This page provides the eToro [Agent Skill](https://agentskills.io). You can use it to connect [OpenClaw](https://openclaw.ai) to your eToro account.

## Usage

Send this prompt to your agent to install the skill:

```
Please install the skill at https://skills.bullaware.com/etoro-api/SKILL.md and follow the instructions.
```

## What this skill enables

* Allow **OpenClaw** to access your portfolio and trade on your behalf (depending on your keys permissions).
* Market data access (instruments, prices, historical data)
* Portfolio and trading information
* Social and feed features
* Trade execution (demo and real, based on key permissions)

## Skill details

* Name: eToro API
* Version: 1.0.0
* Base URL: `https://public-api.etoro.com/api/v1`
* Authentication: `x-api-key`, `x-user-key`, and `x-request-id` headers
