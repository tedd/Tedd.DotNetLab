#pragma once
#include <stdexcept>
#include <string>
#include <utility>

struct Failure : std::runtime_error {
    std::string code;
    Failure(std::string code,std::string message):std::runtime_error(message),code(std::move(code)){}
};
