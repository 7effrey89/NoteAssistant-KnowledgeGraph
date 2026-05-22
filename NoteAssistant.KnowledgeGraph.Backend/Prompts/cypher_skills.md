# Cypher Skill Additions -- Query Intent + Output Descriptions

---

# Query Documentation Format

For every Cypher query, include:

1. **Natural Language Question**
2. **Cypher Query**
3. **What This Query Does**
4. **Expected Output**

Example format:

```md
## Find All Users

### Natural Language Question
"Show me all users in the database."

### Cypher Query

MATCH (u:User)
RETURN u

### What This Query Does
Finds all nodes labeled `User`.

### Expected Output
Returns every `User` node and its properties.
```

---

# Expanded Canonical Examples

## Find All Users

### Natural Language Question

"Show me every user in the system."

### Cypher Query

```cypher id="2z7fkn"
MATCH (u:User)
RETURN u
```

### What This Query Does

Searches the graph for all nodes labeled `User`.

### Expected Output

Returns every `User` node along with all stored properties.

---

## Find User By Email

### Natural Language Question

"Find the user whose email is [alice@example.com](mailto:alice@example.com)."

### Cypher Query

```cypher id="x5rqz2"
MATCH (u:User {email: $email})
RETURN u
```

### What This Query Does

Looks up a specific user node using the indexed `email` property.

### Expected Output

Returns the matching `User` node if it exists.

---

## Find Friends Of A User

### Natural Language Question

"Who are Alice's friends?"

### Cypher Query

```cypher id="6ep7bo"
MATCH (u:User {name:"Alice"})-[:FRIEND_OF]->(f)
RETURN f
```

### What This Query Does

Traverses outgoing `FRIEND_OF` relationships from Alice.

### Expected Output

Returns all connected friend nodes.

---

## Multi-Hop Traversal

### Natural Language Question

"Show me friends-of-friends up to 3 degrees away."

### Cypher Query

```cypher id="g98v8h"
MATCH (u:User)-[:FRIEND_OF*1..3]->(f)
RETURN DISTINCT f
```

### What This Query Does

Traverses the social graph between 1 and 3 hops away.

### Expected Output

Returns unique users reachable within three friendship connections.

---

## Count Users By Country

### Natural Language Question

"How many users are there per country?"

### Cypher Query

```cypher id="7rzpvu"
MATCH (u:User)
RETURN u.country, count(*) AS users
```

### What This Query Does

Groups users by country and counts them.

### Expected Output

Returns each country with the total number of users associated with it.

Example:

```text id="u61f8n"
UK       | 120
USA      | 340
Germany  | 87
```

---

## Create A User

### Natural Language Question

"Create a new user named Alice."

### Cypher Query

```cypher id="8t3z87"
CREATE (u:User {name:"Alice"})
RETURN u
```

### What This Query Does

Creates a new node labeled `User`.

### Expected Output

Returns the newly created node and its properties.

---

## Upsert A User

### Natural Language Question

"Create the user if they don't exist already."

### Cypher Query

```cypher id="tjnv6n"
MERGE (u:User {email:$email})
SET u += $properties
RETURN u
```

### What This Query Does

Ensures only one user exists for the provided email.

If the user exists:

* updates properties

If the user does not exist:

* creates a new node

### Expected Output

Returns the created or updated `User` node.

---

## Connect Two Users

### Natural Language Question

"Make Alice friends with Bob."

### Cypher Query

```cypher id="84d7fw"
MATCH (a:User {name:"Alice"})
MATCH (b:User {name:"Bob"})
MERGE (a)-[:FRIEND_OF]->(b)
```

### What This Query Does

Creates a friendship relationship between two existing users.

### Expected Output

Returns relationship creation statistics such as:

* relationships created
* properties set

---

## Recommendation Query

### Natural Language Question

"Recommend users similar to Alice based on shared purchases."

### Cypher Query

```cypher id="mbj7c9"
MATCH (u:User)-[:PURCHASED]->(p)<-[:PURCHASED]-(other)
WHERE u.id = $user_id
AND other <> u
RETURN other, count(*) AS similarity
ORDER BY similarity DESC
LIMIT 10
```

### What This Query Does

Finds users who purchased the same products as the target user.

Calculates similarity using shared purchases.

### Expected Output

Returns:

* similar users
* similarity scores
* ranked recommendations

Example:

```text id="l2r8ao"
Bob     | 14
Carol   | 11
Dave    | 8
```

---

## GraphRAG Entity Expansion

### Natural Language Question

"Retrieve graph context related to GraphRAG."

### Cypher Query

```cypher id="mdibk5"
MATCH (e:Entity {name:$entity})
MATCH (e)-[:RELATED_TO*1..2]-(context)
RETURN DISTINCT context
LIMIT 20
```

### What This Query Does

Expands outward from a seed entity through nearby semantic relationships.

Useful for:

* GraphRAG
* context expansion
* retrieval augmentation

### Expected Output

Returns related entities, concepts, and contextual nodes connected to the source entity.

---

## Conversation Memory Recall

### Natural Language Question

"What topics has the user discussed recently?"

### Cypher Query

```cypher id="o8zn7e"
MATCH (u:User)-[:MENTIONED]->(topic)
RETURN topic
ORDER BY topic.last_seen DESC
LIMIT 10
```

### What This Query Does

Finds topics previously mentioned by a user.

Ranks results by recency.

### Expected Output

Returns the most recently discussed topics associated with the user.

---

## Shortest Path Query

### Natural Language Question

"What is the shortest connection path between Alice and Bob?"

### Cypher Query

```cypher id="yffk8q"
MATCH p = shortestPath(
  (a:User {name:"Alice"})-[:FRIEND_OF*]->(b:User {name:"Bob"})
)
RETURN p
```

### What This Query Does

Computes the shortest graph traversal path between two users.

### Expected Output

Returns:

* the shortest path
* intermediate nodes
* traversed relationships

---

# Agent Query Generation Rules

When generating Cypher automatically:

Always include:

* a natural language interpretation
* a concise explanation
* an expected output description

This improves:

* explainability
* debugging
* Text2Cypher quality
* agent transparency
* human verification

---

# Recommended Output Style

Preferred structure:

```md
### Natural Language Question
...

### Cypher Query
...

### What This Query Does
...

### Expected Output
...
```

Avoid returning raw Cypher without explanation unless explicitly requested.
