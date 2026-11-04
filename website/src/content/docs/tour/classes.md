---
title: Classes
description: Classes, dataclasses, inheritance, properties and special methods.
sidebar:
  order: 5
---

## Dataclasses

`@dataclass` makes a class from its fields, with a constructor, structural equality and a readable display:

```taipan
@dataclass
class Point:
    x: float
    y: float

    def distance_to(self, other: Point) -> float:
        return ((self.x - other.x) ** 2 + (self.y - other.y) ** 2) ** 0.5

let p = Point(0, 0)
let q = Point(x=3, y=4)
print(q, p.distance_to(q), p == Point(0, 0))
```

```output
Point(x=3.0, y=4.0) 5.0 True
```

`@dataclass(frozen=True)` makes the fields read-only, and `@dataclass(order=True)` adds `<` and the other comparisons.

## Plain classes

A plain class declares its fields and sets them in `__init__`. Every field must be set before `__init__` returns, unless it has a default:

```taipan
class Account:
    owner: str
    balance: int = 0

    def __init__(self, owner: str):
        self.owner = owner

    def deposit(self, amount: int):
        if amount <= 0:
            raise ValueError("deposit must be positive")
        self.balance += amount

let a = Account("ada")
a.deposit(50)
print(a.owner, a.balance)
```

```output
ada 50
```

Plain classes compare by identity, as in Python.

## Inheritance

A class derives from one base class. An overriding method is marked `@override`, so renaming a base method can't silently leave an override behind:

```taipan
class Animal:
    name: str

    def __init__(self, name: str):
        self.name = name

    def speak(self) -> str:
        return "..."

class Dog(Animal):
    @override
    def speak(self) -> str:
        return f"{self.name} says woof"

class Cat(Animal):
    @override
    def speak(self) -> str:
        return f"{self.name} says meow"

let pets = [Dog("rex"), Cat("tom")]
for pet in pets:
    print(pet.speak())
    if isinstance(pet, Dog):
        print("  good dog")
```

```output
rex says woof
  good dog
tom says meow
```

`[Dog("rex"), Cat("tom")]` is a `list[Animal]`, the elements' nearest common base. `isinstance` narrows a value to the class it tests, and `super().method(…)` calls the base class's method. `@abstractmethod` marks a method subclasses must define, and `@final` a class or method that can't be extended.

## Properties, static and class methods

```taipan
class Temperature:
    celsius: float

    def __init__(self, celsius: float):
        self.celsius = celsius

    @property
    def fahrenheit(self) -> float:
        return self.celsius * 9 / 5 + 32

    @fahrenheit.setter
    def fahrenheit(self, value: float):
        self.celsius = (value - 32) * 5 / 9

    @staticmethod
    def freezing() -> Temperature:
        return Temperature(0)

let t = Temperature.freezing()
t.fahrenheit = 212
print(t.celsius, t.fahrenheit)
```

```output
100.0 212.0
```

## Special methods

Python's special methods give a class operators, display, hashing and more: `__str__`, `__repr__`, `__eq__`, `__hash__`, `__lt__` and the other comparisons, `__add__` and the other arithmetic operators, `__len__`, `__getitem__`, `__setitem__`, `__contains__`, `__iter__`, `__call__`, and `__enter__`/`__exit__` for `with`.

```taipan
@dataclass(frozen=True)
class Money:
    cents: int

    def __add__(self, other: Money) -> Money:
        return Money(self.cents + other.cents)

    def __lt__(self, other: Money) -> bool:
        return self.cents < other.cents

    def __str__(self) -> str:
        return f"${self.cents // 100}.{self.cents % 100:02}"

let prices = [Money(250), Money(99), Money(1000)]
print(prices[0] + prices[1], min(prices), sorted(prices)[-1])
```

```output
$3.49 $0.99 $10.00
```

Each special method's signature is checked: a `__lt__` that doesn't return `bool`, say, is an error.

Next: [Enums and match](../enums-and-match/).
