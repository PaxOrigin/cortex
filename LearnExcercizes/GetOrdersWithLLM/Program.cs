using System.ComponentModel;
using Microsoft.Extensions.AI;
using OllamaSharp;


const string systemPrompt = "You are a helpful assistant for an e-commerce platform. Your task is to assist users in retrieving their order information based on their email address. Never invent order data. Always call GetOrdersByCusomerMail to retrieve orders. If the user asks for completed orders, call the function again with includeCompleted set to true.";
const string OllamaModel = "Qwen2.5";
const string OllamaUrl = "http://localhost:11434";
using IChatClient client = new ChatClientBuilder(new OllamaApiClient(OllamaUrl, OllamaModel))
.UseFunctionInvocation()
.Build();
List<ChatMessage> messages = new List<ChatMessage>
    {
        new ChatMessage(ChatRole.System, systemPrompt)
    };

AIFunction GetOrdersByCusomerMailFunction = AIFunctionFactory.Create(GetOrdersByCusomerMail);
AIFunction GetEmailFromUserFunction = AIFunctionFactory.Create(GetEmailFromUser);

List<AITool> tools = new List<AITool>
{
    GetOrdersByCusomerMailFunction,
    GetEmailFromUserFunction
};

ChatOptions options = new ChatOptions
{
    Tools = tools,
    MaxOutputTokens = 2048
};

// main loop
while (true)
{

    Console.WriteLine("Start. (Quit to exit)");
    var userPrompt = Console.ReadLine() ?? string.Empty;
    if (userPrompt.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }
    messages.Add(new ChatMessage(ChatRole.User, userPrompt));
    var response = string.Empty;
    try
    {
        await foreach (var update in client.GetStreamingResponseAsync(messages, options))
        {
            Console.Write(update.Text);
            response += update.Text;
        }
        messages.Add(new ChatMessage(ChatRole.Assistant, response));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error occurred: {ex.Message}");
        messages.Add(new ChatMessage(ChatRole.Assistant, $"Error occurred: {ex.Message}"));
    }
    finally
    {
        response = string.Empty;
        Console.WriteLine("\n");
    }

}

Shutdown(string.Join("\n", messages.Where(p => p.Role != ChatRole.System).Select(m => $"{m.Role}: {m.Text}")));

/// <summary>
/// This method accepts the mail of a customer and returns a list of their orders.
/// If the includeCompleted parameter is set to true, it will also include orders with a status of "Closed".
/// </summary>
/// <param name="email">The email of the customer whose orders are to be retrieved.</param>
/// <param name="includeCompleted">A boolean flag indicating whether to include completed orders (status
/// "Closed") in the result. Default is false.</param>
/// <returns>A list of orders associated with the specified customer email, filtered based on the includeCompleted flag.</returns>
/// <exception cref="ArgumentException">Thrown when the email parameter is null or empty.</exception>
/// <exception cref="InvalidOperationException">Thrown when no customer is found with the provided email
/// or when there are no orders associated with the customer.</exception>
[Description("This method accepts the mail of a customer and returns a list of their orders. If the includeCompleted parameter is set to true, it will also include orders with a status of 'Closed'.")]
List<Order> GetOrdersByCusomerMail(
    [Description("The email of the customer whose orders are to be retrieved.")] string email,
    [Description("A boolean flag indicating whether to include completed orders (status 'Closed') in the result. Default is false.")] bool includeCompleted = false)
{
    ArgumentException.ThrowIfNullOrEmpty(email);
    var customer = GetCustomers().FirstOrDefault(c => c.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
    if (customer is null)
    {
        throw new InvalidOperationException($"No customer found with email: {email}");
    }
    var orders = GetOrders().Where(o => o.CustomerId == customer.Id);
    if (!includeCompleted)
    {
        orders = orders.Where(o => !o.Status.Equals("Closed", StringComparison.OrdinalIgnoreCase));
    }
    return orders.ToList();
}

/// <summary>
/// This method asks the user for his email and validates it.
/// If the email is not valid, it throws an ArgumentException with a message indicating that the email format is invalid.
/// </summary>
/// <returns>The email entered by the user if it is valid.</returns>
/// <exception cref="ArgumentException">Thrown when the email format is invalid.</exception>
[Description("This method asks the user for his email and validates it. If the email is not valid, it throws an ArgumentException with a message indicating that the email format is invalid.")]
string GetEmailFromUser()
{
    Console.Write("Enter the customer's email: ");
    var email = Console.ReadLine() ?? string.Empty;
    try
    {
        var _ = new System.Net.Mail.MailAddress(email);
    }
    catch
    {
        throw new ArgumentException("Invalid email format. Please enter a valid email address.");
    }
    return email;
}

void Shutdown(string chatHistory)
{
    Console.WriteLine(string.Join("-", Enumerable.Repeat("-", 50)));
    Console.WriteLine("Shutting down the application...");
    Console.WriteLine("Chat history:");
    Console.WriteLine(chatHistory);
    Console.WriteLine(string.Join("-", Enumerable.Repeat("-", 50)));
    Environment.Exit(0);
}


IEnumerable<Order> GetOrders()
{
    return new List<Order>
    {
        new Order("Laptop", 1, "Shipped", 1, 101),
        new Order("Phone", 2, "Processing", 1, 102),
        new Order("Headphones", 3, "Delivered", 1, 101),
        new Order("Monitor", 4, "Closed", 1, 103),
        new Order("Keyboard", 5, "Shipped", 1, 104),
        new Order("Mouse", 6, "Processing", 2, 101),
        new Order("Webcam", 7, "Delivered", 1, 105),
        new Order("USB Hub", 8, "Closed", 1, 106),
        new Order("SSD Drive", 9, "Shipped", 1, 102),
        new Order("RAM Module", 10, "Processing", 2, 103),
        new Order("Graphics Card", 11, "Delivered", 1, 101),
        new Order("Motherboard", 12, "Closed", 1, 104),
        new Order("Power Supply", 13, "Shipped", 1, 105),
        new Order("CPU Cooler", 14, "Processing", 1, 106),
        new Order("Case Fan", 15, "Delivered", 3, 102),
        new Order("Ethernet Cable", 16, "Closed", 2, 103),
        new Order("HDMI Cable", 17, "Shipped", 1, 101),
        new Order("Desk Lamp", 18, "Processing", 1, 104),
        new Order("Microphone", 21, "Shipped", 1, 105),
        new Order("Speakers", 22, "Closed", 2, 106),
        new Order("Drawing Tablet", 23, "Delivered", 1, 102),
        new Order("Stream Deck", 24, "Processing", 1, 103),
        new Order("Chair Mat", 19, "Shipped", 1, 101),
        new Order("Surge Protector", 20, "Closed", 1, 104),
    };
}

IEnumerable<Customer> GetCustomers()
{
    return new List<Customer>
    {
        new Customer(101, "Giuseppe", "Fernardi", "giuseppe.fernardi@email.it"),
        new Customer(102, "Marco", "Bianchi", "marco.bianchi@email.it"),
        new Customer(103, "Laura", "Conti", "laura.conti@email.it"),
        new Customer(104, "Alessia", "Ricci", "alessia.ricci@email.it"),
        new Customer(105, "Davide", "Moretti", "davide.moretti@email.it"),
        new Customer(106, "Chiara", "Esposito", "chiara.esposito@email.it"),
    };
}

record Customer(int Id, string Name, string Surname, string Email);
record Order(string Item, int Id, string Status, int Quantity, int CustomerId);

